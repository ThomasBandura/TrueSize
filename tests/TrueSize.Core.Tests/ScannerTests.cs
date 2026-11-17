using TrueSize.Core.Model;
using TrueSize.Core.Scanning;
using TrueSize.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace TrueSize.Core.Tests;

[Collection(TestTreeCollection.Name)]
public sealed class ScannerTests(TestTrees trees, ITestOutputHelper output)
{
    private static readonly ScanOptions Fast = new();
    private static readonly ScanOptions FastNoLinks = new() { ResolveLinkCounts = false };
    private static readonly ScanOptions WithStreams = new() { IncludeAlternateStreams = true };

    private static ScanTree Scan(IVolumeScanner scanner, TestTree fixture, ScanOptions options)
    {
        var result = scanner.Scan(fixture.Root, options);
        return result.Tree;
    }

    private static int Node(ScanTree tree, string relative)
    {
        int node = tree.Find(relative);
        Assert.True(node >= 0, $"Node '{relative}' not found.");
        return node;
    }

    public static TheoryData<string, string> ScannersAndFileSystems => new()
    {
        { "Directory", "NTFS" }, { "Directory", "ReFS" }, { "Reference", "NTFS" }, { "Reference", "ReFS" },
    };

    private static IVolumeScanner Create(string name) => name == "Directory" ? new DirectoryScanner() : new ReferenceScanner();

    [Theory]
    [MemberData(nameof(ScannersAndFileSystems))]
    public void ReparsePoints_AreMarked_AndNotFollowed(string scanner, string fs)
    {
        if (!trees.Has(fs)) return;
        var fixture = trees.Get(fs);
        var tree = Scan(Create(scanner), fixture, Fast);

        int junction = Node(tree, "junction");
        var flags = tree.GetFlags(junction);
        Assert.True(flags.HasFlag(NodeFlags.Junction | NodeFlags.ReparsePoint | NodeFlags.NotFollowed | NodeFlags.Directory));
        Assert.Equal(-1, tree.GetFirstChild(junction));
        Assert.Equal(0, tree.GetTotalSize(junction));

        if (fixture.HasSymlinks)
        {
            int symdir = Node(tree, "symdir");
            Assert.True(tree.GetFlags(symdir).HasFlag(NodeFlags.Symlink | NodeFlags.NotFollowed));
            Assert.Equal(-1, tree.GetFirstChild(symdir));

            int symfile = Node(tree, "symfile.bin");
            Assert.True(tree.GetFlags(symfile).HasFlag(NodeFlags.Symlink));
            Assert.Equal(0, tree.GetSize(symfile));
        }
    }

    [Theory]
    [MemberData(nameof(ScannersAndFileSystems))]
    public void Hardlinks_FirstFound_CountsOnceAtSmallestPath(string scanner, string fs)
    {
        if (!trees.Has(fs)) return;
        var tree = Scan(Create(scanner), trees.Get(fs), Fast);

        int linked = Node(tree, @"dirA\linked.bin");
        int copy = Node(tree, @"dirB\linked-copy.bin");
        Assert.Equal(2, tree.GetLinkCount(linked));
        Assert.Equal(TestTree.LinkedSize, tree.GetSize(linked));
        Assert.Equal(TestTree.LinkedSize, tree.GetSize(copy));
        Assert.True(tree.GetFlags(linked).HasFlag(NodeFlags.Hardlink));
        Assert.True(tree.GetFlags(copy).HasFlag(NodeFlags.Hardlink | NodeFlags.HardlinkSecondary));

        Assert.Equal(TestTree.A1Size + TestTree.LinkedSize, tree.GetTotalSize(Node(tree, "dirA")));
        Assert.Equal(0, tree.GetTotalSize(Node(tree, "dirB")));
    }

    [Theory]
    [MemberData(nameof(TestTrees.FileSystems), MemberType = typeof(TestTrees))]
    public void Hardlinks_ModesCanBeReaggregatedWithoutRescan(string fs)
    {
        if (!trees.Has(fs)) return;
        var tree = Scan(new DirectoryScanner(), trees.Get(fs), Fast);
        int dirA = Node(tree, "dirA"), dirB = Node(tree, "dirB");
        long rootFirstFound = tree.GetTotalSize(ScanTree.Root);

        tree.Aggregate(HardlinkMode.Proportional);
        Assert.Equal(TestTree.A1Size + (TestTree.LinkedSize / 2), tree.GetTotalSize(dirA));
        Assert.Equal(TestTree.LinkedSize / 2, tree.GetTotalSize(dirB));
        Assert.Equal(rootFirstFound, tree.GetTotalSize(ScanTree.Root));

        tree.Aggregate(HardlinkMode.Separate);
        Assert.Equal(TestTree.A1Size, tree.GetTotalSize(dirA));
        Assert.Equal(TestTree.LinkedSize, tree.GetHardlinkSize(dirA));
        Assert.Equal(0, tree.GetTotalSize(dirB));
        Assert.Equal(rootFirstFound, tree.GetTotalSize(ScanTree.Root) + tree.GetHardlinkSize(ScanTree.Root));
    }

    [Theory]
    [MemberData(nameof(ScannersAndFileSystems))]
    public void Freeable_ExcludesFilesWithLinksOutsideTheSubtree(string scanner, string fs)
    {
        if (!trees.Has(fs)) return;
        var tree = Scan(Create(scanner), trees.Get(fs), Fast);

        int dirA = Node(tree, "dirA");
        int a1 = Node(tree, @"dirA\a1.bin");
        int linked = Node(tree, @"dirA\linked.bin");
        int external = Node(tree, @"dirC\external.bin");

        // linked.bin has its second link in dirB: deleting dirA alone frees nothing for it.
        Assert.Equal(tree.GetAllocated(dirA) + tree.GetAllocated(a1), tree.GetFreeable(dirA));
        Assert.Equal(tree.GetAllocated(Node(tree, "dirB")), tree.GetFreeable(Node(tree, "dirB")));

        // external.bin has a link outside the scanned root.
        Assert.True(tree.GetFlags(external).HasFlag(NodeFlags.ExternalLinks));
        Assert.Equal(tree.GetAllocated(Node(tree, "dirC")), tree.GetFreeable(Node(tree, "dirC")));

        // At the root both links of linked.bin are inside: only external.bin is missing.
        Assert.Equal(tree.GetAllocated(external), tree.GetTotalAllocated(ScanTree.Root) - tree.GetFreeable(ScanTree.Root));
        Assert.True(tree.GetAllocated(linked) > 0);
        Assert.True(tree.Info.LinkCountsExact);
    }

    [Theory]
    [MemberData(nameof(TestTrees.FileSystems), MemberType = typeof(TestTrees))]
    public void WithoutLinkCounts_DuplicatesInScopeAreStillDetected(string fs)
    {
        if (!trees.Has(fs)) return;
        var tree = Scan(new DirectoryScanner(), trees.Get(fs), FastNoLinks);

        Assert.False(tree.Info.LinkCountsExact);
        Assert.True(tree.GetFlags(Node(tree, @"dirB\linked-copy.bin")).HasFlag(NodeFlags.HardlinkSecondary));

        // A link outside the scope cannot be seen without the link count.
        Assert.False(tree.GetFlags(Node(tree, @"dirC\external.bin")).HasFlag(NodeFlags.ExternalLinks));
    }

    [Theory]
    [MemberData(nameof(ScannersAndFileSystems))]
    public void SparseFile_AllocatedIsMuchSmallerThanSize(string scanner, string fs)
    {
        if (!trees.Has(fs)) return;
        var tree = Scan(Create(scanner), trees.Get(fs), Fast);
        int sparse = Node(tree, "sparse.bin");

        Assert.True(tree.GetFlags(sparse).HasFlag(NodeFlags.Sparse));
        Assert.Equal(TestTree.SparseSize, tree.GetSize(sparse));
        Assert.InRange(tree.GetAllocated(sparse), 1, 1024 * 1024);
    }

    [Theory]
    [MemberData(nameof(ScannersAndFileSystems))]
    public void AlternateStreams_OnlyCountedWhenRequested(string scanner, string fs)
    {
        if (!trees.Has(fs)) return;
        var fixture = trees.Get(fs);

        var without = Scan(Create(scanner), fixture, Fast);
        Assert.Equal(TestTree.AdsMainSize, without.GetSize(Node(without, "ads.txt")));

        var with = Scan(Create(scanner), fixture, WithStreams);
        int ads = Node(with, "ads.txt");
        Assert.Equal(TestTree.AdsMainSize + TestTree.AdsStreamSize, with.GetSize(ads));
        Assert.True(with.GetFlags(ads).HasFlag(NodeFlags.AlternateStreams));
        Assert.True(with.Info.AlternateStreamsIncluded);
    }

    [Theory]
    [MemberData(nameof(ScannersAndFileSystems))]
    public void CompressedAndWof_AllocatedSmallerThanSize(string scanner, string fs)
    {
        if (!trees.Has(fs)) return;
        var fixture = trees.Get(fs);
        var tree = Scan(Create(scanner), fixture, Fast);

        if (fixture.HasCompression)
        {
            int c = Node(tree, "compressed.bin");
            Assert.True(tree.GetFlags(c).HasFlag(NodeFlags.Compressed));
            Assert.Equal(TestTree.CompressibleSize, tree.GetSize(c));
            Assert.True(tree.GetAllocated(c) < TestTree.CompressibleSize / 2, $"compressed allocated {tree.GetAllocated(c)}");
        }

        output.WriteLine($"{fs}: compression={fixture.HasCompression}, wof={fixture.HasWof}, symlinks={fixture.HasSymlinks}, denied={fixture.HasDeniedDirectory}");
        if (fixture.HasWof)
        {
            int w = Node(tree, "wof.bin");
            Assert.True(tree.GetFlags(w).HasFlag(NodeFlags.Wof));
            Assert.Equal(TestTree.CompressibleSize, tree.GetSize(w));
            Assert.True(tree.GetAllocated(w) < TestTree.CompressibleSize / 2, $"wof allocated {tree.GetAllocated(w)}");
        }
    }

    [Theory]
    [MemberData(nameof(ScannersAndFileSystems))]
    public void AccessDenied_IsLoggedAndScanContinues(string scanner, string fs)
    {
        if (!trees.Has(fs)) return;
        var fixture = trees.Get(fs);
        if (!fixture.HasDeniedDirectory) return;

        var result = Create(scanner).Scan(fixture.Root, Fast);
        int denied = Node(result.Tree, "denied");
        Assert.True(result.Tree.GetFlags(denied).HasFlag(NodeFlags.AccessDenied));
        Assert.Contains(result.Errors, e => e.Path.EndsWith("denied", StringComparison.OrdinalIgnoreCase) && e.Win32Error == 5);
        Assert.True(Node(result.Tree, "plain.bin") > 0);
    }

    [Theory]
    [MemberData(nameof(TestTrees.FileSystems), MemberType = typeof(TestTrees))]
    public void DirectoryScanner_MatchesReference_NodeByNode(string fs)
    {
        if (!trees.Has(fs)) return;
        var fixture = trees.Get(fs);
        var fast = Scan(new DirectoryScanner(), fixture, WithStreams);
        var reference = Scan(new ReferenceScanner(), fixture, WithStreams);

        var mismatches = CompareTrees(fast, reference);
        foreach (var m in mismatches)
        {
            output.WriteLine(m);
        }

        Assert.Empty(mismatches);
        Assert.Equal(reference.GetTotalSize(ScanTree.Root), fast.GetTotalSize(ScanTree.Root));
        Assert.Equal(reference.GetTotalAllocated(ScanTree.Root), fast.GetTotalAllocated(ScanTree.Root));
        Assert.Equal(reference.GetFreeable(ScanTree.Root), fast.GetFreeable(ScanTree.Root));
        Assert.Equal(reference.GetFileCount(ScanTree.Root), fast.GetFileCount(ScanTree.Root));
    }

    [Theory]
    [MemberData(nameof(TestTrees.FileSystems), MemberType = typeof(TestTrees))]
    public void Report_FastWithoutLinkCounts_DifferencesToReference(string fs)
    {
        if (!trees.Has(fs)) return;
        var fixture = trees.Get(fs);
        var fast = Scan(new DirectoryScanner(), fixture, FastNoLinks);
        var reference = Scan(new ReferenceScanner(), fixture, Fast);

        output.WriteLine($"[{fs}] ResolveLinkCounts=false vs Reference (documented, not asserted):");
        foreach (var m in CompareTrees(fast, reference))
        {
            output.WriteLine("  " + m);
        }
    }

    [Theory]
    [MemberData(nameof(ScannersAndFileSystems))]
    public void DirectoryIndex_CountsTowardAllocatedOnly(string scanner, string fs)
    {
        if (!trees.Has(fs)) return;
        var tree = Scan(Create(scanner), trees.Get(fs), Fast);
        int big = Node(tree, "bigdir");

        Assert.Equal(0, tree.GetSize(big));
        Assert.Equal(0, tree.GetTotalSize(big));
        Assert.Equal(TestTree.BigDirFileCount, tree.GetFileCount(big));
        output.WriteLine($"{scanner}/{fs}: bigdir allocated = {tree.GetAllocated(big)}");
        if (fs == "NTFS")
        {
            Assert.True(tree.GetAllocated(big) > 0, "NTFS index allocation expected.");
        }
    }

    [Theory]
    [MemberData(nameof(TestTrees.FileSystems), MemberType = typeof(TestTrees))]
    public void WithoutLinkCounts_FileAllocationStillMatchesReference(string fs)
    {
        if (!trees.Has(fs)) return;
        var fixture = trees.Get(fs);
        var fast = Scan(new DirectoryScanner(), fixture, FastNoLinks);
        var reference = Scan(new ReferenceScanner(), fixture, Fast);

        // Catches WOF files, whose directory entry reports 0 allocated bytes.
        for (int i = 0; i < fast.Count; i++)
        {
            if ((fast.GetFlags(i) & NodeFlags.Directory) != 0)
            {
                continue;
            }

            int r = reference.Find(Path.GetRelativePath(fixture.Root, fast.GetPath(i)));
            Assert.True(r >= 0);
            Assert.Equal((fast.GetPath(i), reference.GetAllocated(r)), (fast.GetPath(i), fast.GetAllocated(i)));
        }
    }

    [Fact]
    public void Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new DirectoryScanner().Scan(Path.GetTempPath(), Fast, cancellationToken: cts.Token));
    }

    internal static List<string> CompareTrees(ScanTree actual, ScanTree expected)
    {
        var result = new List<string>();
        var expectedByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < expected.Count; i++)
        {
            expectedByPath[expected.GetPath(i)] = i;
        }

        const NodeFlags ignored = NodeFlags.Error;
        for (int i = 0; i < actual.Count; i++)
        {
            string path = actual.GetPath(i);
            if (!expectedByPath.Remove(path, out int e))
            {
                result.Add($"extra: {path}");
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
            if ((actual.GetFlags(i) & ~ignored) != (expected.GetFlags(e) & ~ignored))
            {
                result.Add($"Flags          {path}: {actual.GetFlags(i)} vs {expected.GetFlags(e)}");
            }
        }

        result.AddRange(expectedByPath.Keys.Select(p => $"missing: {p}"));
        return result;
    }
}
