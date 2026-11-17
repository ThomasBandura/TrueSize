using TrueSize.Core.Model;
using TrueSize.Core.Ntfs;
using TrueSize.Core.Scanning;
using TrueSize.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace TrueSize.Core.Tests;

/// <summary>One VHDX per test run, filled with the standard fixture plus MFT-specific cases.</summary>
public sealed class MftVolumeFixture : IDisposable
{
    public const int ManyLinks = 300;

    public MftVolumeFixture()
    {
        if (!ScannerSelector.IsElevated())
        {
            return;
        }

        Volume = new VhdxVolume();
        Tree = TestTree.Create(Path.Combine(Volume.Root, "data"));

        // Enough hard links to push $FILE_NAME attributes into extension records ($ATTRIBUTE_LIST).
        string dir = Path.Combine(Tree.Root, "manylinks");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "target.bin");
        File.WriteAllBytes(target, new byte[50_000]);
        for (int i = 0; i < ManyLinks; i++)
        {
            if (!Interop.Native.CreateHardLink(Path.Combine(dir, $"link-with-a-long-name-to-fill-records-{i:D4}.bin"), target, 0))
            {
                throw new IOException("CreateHardLink failed.");
            }
        }

        // Heavily fragmented file: interleave appends with another file so the data has many runs.
        string a = Path.Combine(Tree.Root, "fragmented.bin");
        string b = Path.Combine(Tree.Root, "filler.bin");
        var block = new byte[64 * 1024];
        Random.Shared.NextBytes(block);
        for (int i = 0; i < 200; i++)
        {
            using (var fa = new FileStream(a, FileMode.Append, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { fa.Write(block); }
            using (var fb = new FileStream(b, FileMode.Append, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { fb.Write(block); }
        }
    }

    public VhdxVolume? Volume { get; }

    public TestTree? Tree { get; }

    public void Dispose()
    {
        Tree?.Dispose();
        Volume?.Dispose();
    }
}

public sealed class NtfsMftScannerTests(MftVolumeFixture fixture, ITestOutputHelper output) : IClassFixture<MftVolumeFixture>
{
    private static readonly ScanOptions WithStreams = new() { IncludeAlternateStreams = true };

    // Flags that only one side can know: the MFT sees WOF/sparse/reparse on WOF files (the WOF filter hides them from
    // enumeration); FILE_ATTRIBUTE_COMPRESSED on directories and cloud/offline attributes live in $STANDARD_INFORMATION,
    // whose attribute field is not documented.
    private const NodeFlags NotComparable =
        NodeFlags.Error | NodeFlags.Wof | NodeFlags.Sparse | NodeFlags.ReparsePoint | NodeFlags.Compressed |
        NodeFlags.Offline | NodeFlags.CloudNotLocal | NodeFlags.CloudNotPopulated | NodeFlags.LinkCountUnknown | NodeFlags.Encrypted;

    [ElevatedFact]
    public void Fixture_MatchesReference_NodeByNode()
    {
        var mft = new NtfsMftScanner().Scan(fixture.Tree!.Root, WithStreams);
        var reference = new ReferenceScanner().Scan(fixture.Tree.Root, WithStreams);
        AssertSame(mft.Tree, reference.Tree);
        Assert.Empty(mft.Errors);
    }

    [ElevatedFact]
    public void WholeVolume_MatchesReference_NodeByNode()
    {
        var mft = new NtfsMftScanner().Scan(fixture.Volume!.Root, WithStreams);
        var reference = new ReferenceScanner().Scan(fixture.Volume.Root, WithStreams);
        output.WriteLine($"reference errors: {string.Join(", ", reference.Errors.Select(e => $"{e.Path}: {e.Win32Error}"))}");
        AssertSame(mft.Tree, reference.Tree);
    }

    [ElevatedFact]
    public void ManyHardLinks_InExtensionRecords_AreAllFound()
    {
        var tree = new NtfsMftScanner().Scan(fixture.Tree!.Root, new ScanOptions()).Tree;
        int dir = tree.Find("manylinks");
        Assert.Equal(MftVolumeFixture.ManyLinks + 1, tree.GetFileCount(dir));
        int target = tree.Find(@"manylinks\target.bin");
        Assert.Equal(MftVolumeFixture.ManyLinks + 1, tree.GetLinkCount(target));
        Assert.Equal(50_000, tree.GetTotalSize(dir));
    }

    [ElevatedFact]
    public void DirectoryScannerElevated_MatchesMft_OnWholeVolume()
    {
        var mft = new NtfsMftScanner().Scan(fixture.Volume!.Root, new ScanOptions());
        var directory = new DirectoryScanner().Scan(fixture.Volume.Root, new ScanOptions());
        AssertSame(mft.Tree, directory.Tree);
    }

    [ElevatedFact]
    public void Live_ProgramFilesDotnet_MatchesDirectoryScanner()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        if (!Directory.Exists(path)) return;

        var mft = new NtfsMftScanner().Scan(path, new ScanOptions());
        var directory = new DirectoryScanner().Scan(path, new ScanOptions());
        output.WriteLine($"MFT {mft.Duration.TotalMilliseconds:F0} ms, Directory {directory.Duration.TotalMilliseconds:F0} ms");
        AssertSame(mft.Tree, directory.Tree);
    }

    [ElevatedFact]
    public void Live_Windows_DifferencesAreReported()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var mft = new NtfsMftScanner().Scan(path, new ScanOptions());
        var directory = new DirectoryScanner().Scan(path, new ScanOptions());
        output.WriteLine($"MFT {mft.Duration.TotalMilliseconds:F0} ms, Directory {directory.Duration.TotalMilliseconds:F0} ms, directory errors {directory.Errors.Count}");
        output.WriteLine($"root size {mft.Tree.GetTotalSize(0):N0} vs {directory.Tree.GetTotalSize(0):N0}, allocated {mft.Tree.GetTotalAllocated(0):N0} vs {directory.Tree.GetTotalAllocated(0):N0}, files {mft.Tree.GetFileCount(0):N0} vs {directory.Tree.GetFileCount(0):N0}");
        foreach (var line in Compare(mft.Tree, directory.Tree).Take(60))
        {
            output.WriteLine(line);
        }
    }

    /// <summary>
    /// Verifies the undocumented $FILE_NAME reparse tag copy (NtfsLayout.FileNameReparseTagUndocumented): every reparse
    /// tag reported by directory enumeration in the user profile (OneDrive placeholders, AppExecLinks, ...) must match.
    /// </summary>
    [ElevatedFact]
    public void Live_UserProfile_ReparseTagsMatchEnumeration()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var options = new ScanOptions { ResolveLinkCounts = false, EnumerateCloudDirectories = true, ResolveAllReparseTags = true };
        var mft = new NtfsMftScanner().Scan(path, options).Tree;
        var directory = new DirectoryScanner().Scan(path, options).Tree;

        var mftByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < mft.Count; i++)
        {
            mftByPath[mft.GetPath(i)] = i;
        }

        int compared = 0;
        var mismatches = new List<string>();
        for (int i = 0; i < directory.Count; i++)
        {
            if (!directory.TryGetReparseTag(i, out uint expected) || !mftByPath.TryGetValue(directory.GetPath(i), out int m))
            {
                continue;
            }

            compared++;
            if (!mft.TryGetReparseTag(m, out uint actual) || actual != expected)
            {
                mismatches.Add($"{directory.GetPath(i)}: 0x{actual:X8} vs 0x{expected:X8}");
            }
        }

        var timings = NtfsMftScanner.LastTimings;
        output.WriteLine($"compared {compared:N0} reparse tags; nonresident reparse {timings.NonResidentReparseTotal:N0}, cluster reads {timings.NonResidentReparseRead:N0}, FILE_NAME mismatches {timings.FileNameTagMismatches}");
        foreach (var line in mismatches.Take(40))
        {
            output.WriteLine(line);
        }

        Assert.Empty(mismatches);
        Assert.Equal(0, timings.FileNameTagMismatches);
    }

    [Fact]
    public void Fixup_RestoresSavedWords_AndDetectsTornWrites()
    {
        var record = new byte[1024];
        // MULTI_SECTOR_HEADER: signature, USA offset 48, USA size 3 (USN + 2 saved words).
        "FILE"u8.CopyTo(record);
        BitConverter.GetBytes((ushort)48).CopyTo(record, 4);
        BitConverter.GetBytes((ushort)3).CopyTo(record, 6);
        BitConverter.GetBytes((ushort)0x1234).CopyTo(record, 48); // USN
        BitConverter.GetBytes((ushort)0xAAAA).CopyTo(record, 50); // saved word of stride 1
        BitConverter.GetBytes((ushort)0xBBBB).CopyTo(record, 52); // saved word of stride 2
        BitConverter.GetBytes((ushort)0x1234).CopyTo(record, 510);
        BitConverter.GetBytes((ushort)0x1234).CopyTo(record, 1022);

        var copy = (byte[])record.Clone();
        Assert.True(NtfsRecord.ApplyFixup(copy));
        Assert.Equal(0xAAAA, BitConverter.ToUInt16(copy, 510));
        Assert.Equal(0xBBBB, BitConverter.ToUInt16(copy, 1022));

        BitConverter.GetBytes((ushort)0x9999).CopyTo(record, 1022);
        Assert.False(NtfsRecord.ApplyFixup(record));
    }

    [Fact]
    public void MappingPairs_DecodeDocumentedExample()
    {
        // Documented example: single run of 8 clusters at LCN 128, LowestVcn 0 -> count byte v=1, l=2 (128 needs 2 signed bytes).
        var attribute = new byte[80];
        BitConverter.GetBytes(NtfsLayout.DATA).CopyTo(attribute, 0);
        BitConverter.GetBytes(80).CopyTo(attribute, 4);
        attribute[8] = NtfsLayout.NONRESIDENT_FORM;
        BitConverter.GetBytes((ushort)64).CopyTo(attribute, NtfsLayout.AttrMappingPairsOffset);
        attribute[64] = 0x21;            // v = 1, l = 2
        attribute[65] = 8;               // VCN delta
        attribute[66] = 0x80;            // LCN delta low byte
        attribute[67] = 0x00;            // LCN delta high byte (positive)
        attribute[68] = 0x11;            // second run: v = 1, l = 1
        attribute[69] = 4;               // 4 clusters
        attribute[70] = 0xF0;            // LCN delta -16 -> 112
        attribute[71] = 0x01;            // sparse run: v = 1, l = 0
        attribute[72] = 2;
        attribute[73] = 0x00;            // end

        var extents = new List<Extent>();
        Assert.True(new NtfsRecord.Attribute(attribute).DecodeMappingPairs(extents));
        Assert.Equal([new Extent(0, 128, 8), new Extent(8, 112, 4), new Extent(12, -1, 2)], extents);
    }

    private void AssertSame(ScanTree actual, ScanTree expected)
    {
        var differences = Compare(actual, expected);
        foreach (var line in differences.Take(80))
        {
            output.WriteLine(line);
        }

        Assert.Empty(differences);
    }

    private static List<string> Compare(ScanTree actual, ScanTree expected)
    {
        var result = new List<string>();
        var expectedByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < expected.Count; i++)
        {
            expectedByPath[expected.GetPath(i)] = i;
        }

        for (int i = 0; i < actual.Count; i++)
        {
            string path = actual.GetPath(i);
            if (!expectedByPath.Remove(path, out int e))
            {
                result.Add($"only MFT: {path} ({actual.GetFlags(i)})");
                continue;
            }

            void Check(string what, long a, long b)
            {
                if (a != b)
                {
                    result.Add($"{what,-14} {path}: {a} vs {b}");
                }
            }

            Check("Size", actual.GetSize(i), expected.GetSize(e));
            Check("Allocated", actual.GetAllocated(i), expected.GetAllocated(e));
            Check("TotalSize", actual.GetTotalSize(i), expected.GetTotalSize(e));
            Check("TotalAllocated", actual.GetTotalAllocated(i), expected.GetTotalAllocated(e));
            Check("Freeable", actual.GetFreeable(i), expected.GetFreeable(e));
            Check("LinkCount", actual.GetLinkCount(i), expected.GetLinkCount(e));
            if (actual.GetFileId(i) != expected.GetFileId(e) && i != ScanTree.Root)
            {
                result.Add($"FileId         {path}: {actual.GetFileId(i)} vs {expected.GetFileId(e)}");
            }

            // Tag-derived flags are unknown on purpose when the MFT scanner skipped a nonresident tag (ResolveAllReparseTags off).
            bool tagUnknown = (actual.GetFlags(i) & NodeFlags.ReparseTagUnknown) != 0;
            var ignored = NotComparable | NodeFlags.ReparseTagUnknown |
                          (tagUnknown ? NodeFlags.Symlink | NodeFlags.Junction | NodeFlags.CloudPlaceholder | NodeFlags.Dedup | NodeFlags.NotFollowed : NodeFlags.None);
            if ((actual.GetFlags(i) & ~ignored) != (expected.GetFlags(e) & ~ignored))
            {
                result.Add($"Flags          {path}: {actual.GetFlags(i)} vs {expected.GetFlags(e)}");
            }

            // Every tag visible to enumeration must match (the MFT additionally sees WOF tags hidden by the filter).
            if (!tagUnknown && expected.TryGetReparseTag(e, out uint expectedTag) && (!actual.TryGetReparseTag(i, out uint actualTag) || actualTag != expectedTag))
            {
                actual.TryGetReparseTag(i, out actualTag);
                result.Add($"ReparseTag     {path}: 0x{actualTag:X8} vs 0x{expectedTag:X8}");
            }
        }

        result.AddRange(expectedByPath.Keys.Select(p => $"only reference: {p}"));
        return result;
    }
}
