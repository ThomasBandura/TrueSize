using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TrueSize.Core.Interop;
using TrueSize.Core.Model;
using TrueSize.Core.Ntfs;

namespace TrueSize.Core.Scanning;

/// <summary>
/// Fast NTFS scanner (administrator only): reads $MFT sequentially from the raw volume, then builds the tree for the
/// requested path from the $FILE_NAME parent references. Link counts, alternate streams and directory index sizes come
/// for free. NTFS metadata files (records below 16, including $Extend) are not part of the tree.
/// </summary>
public sealed class NtfsMftScanner : IVolumeScanner
{
    public string Name => "NtfsMft";

    public ScanResult Scan(string path, ScanOptions options, ScanProgress? progress = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        progress ??= new ScanProgress();
        string root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var volume = VolumeInfo.FromPath(root);
        if (!string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"NtfsMftScanner requires NTFS, '{root}' is on {volume.FileSystem}.");
        }

        int rootRecord = GetRecordNumber(root);
        var errors = new List<ScanError>();
        var phase = Stopwatch.StartNew();

        using var mft = MftVolume.Open(volume.Root);
        var openTime = phase.Elapsed;
        var table = new MftTable(mft.RecordCount);
        long lastReported = 0;
        mft.ReadAllRecords(
            (number, record) =>
            {
                table.Add(number, record);
                if (number - lastReported >= 65536)
                {
                    progress.Add(number - lastReported, 0, 0);
                    lastReported = number;
                }
            },
            cancellationToken);
        var readTime = phase.Elapsed - openTime;

        foreach (var (record, _) in table.InvalidRecords)
        {
            errors.Add(new ScanError($"MFT record {record}", 0, "MftRecordFixup"));
            progress.AddError();
        }

        if (!table.IsInUse(rootRecord) || !table.IsDirectory(rootRecord))
        {
            throw new InvalidDataException($"MFT record {rootRecord} for '{root}' is not an in-use directory.");
        }

        var beforeIndex = phase.Elapsed;
        table.ApplyFileNameReparseTags();
        var children = BuildChildIndex(table);
        int reparseReads = ResolveReachableReparseTags(mft, table, children, rootRecord, options, errors);
        var indexTime = phase.Elapsed - beforeIndex;
        var builder = new ScanTreeBuilder();
        var rootEntry = table.ToEntry(rootRecord, options.IncludeAlternateStreams);
        rootEntry.Flags &= ~NodeFlags.NotFollowed; // the scan root is always enumerated, like the directory scanners
        int rootNode = builder.AddRoot(root, rootEntry);

        BuildTree(table, children, builder, rootNode, rootRecord, options, progress, cancellationToken);

        var tree = builder.Build(new ScanTreeInfo(
            root, volume.Root, volume.SerialNumber, volume.FileSystem, Name, LinkCountsExact: true, options.IncludeAlternateStreams));
        tree.Aggregate(options.HardlinkMode);
        LastTimings = new MftTimings(openTime, readTime, indexTime, phase.Elapsed - beforeIndex - indexTime, mft.RecordCount, mft.RecordSize,
            table.NonResidentReparse.Count, reparseReads, sw.Elapsed, table.FileNameTagMismatches);
        return new ScanResult(tree, errors, sw.Elapsed);
    }

    internal readonly record struct MftTimings(
        TimeSpan Open, TimeSpan ReadAndParse, TimeSpan ChildIndexAndReparse, TimeSpan TreeAndAggregate, long Records, int RecordSize,
        int NonResidentReparseTotal, int NonResidentReparseRead, TimeSpan Total, int FileNameTagMismatches);

    /// <summary>Phase timings of the most recent scan on this thread (diagnostics for the benchmark tool).</summary>
    [ThreadStatic]
    internal static MftTimings LastTimings;

    /// <summary>NTFS file reference of the directory: the low 48 bits of the 128-bit file id are the MFT segment number.</summary>
    private static unsafe int GetRecordNumber(string directory)
    {
        string extended = FileQueries.ToExtendedPath(directory.EndsWith('\\') ? directory : directory + "\\") + "\0";
        fixed (char* p = extended)
        {
            using var handle = FileQueries.OpenDirectoryForListing(p, followReparsePoint: true);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot open '{directory}'.");
            }

            FILE_ID_INFO info;
            if (!Native.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo, &info, (uint)sizeof(FILE_ID_INFO)))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"FileIdInfo failed for '{directory}'.");
            }

            if (info.FileId.High != 0)
            {
                throw new InvalidDataException($"Unexpected 128-bit file id for NTFS: {info.FileId.High:x16}{info.FileId.Low:x16}.");
            }

            return checked((int)(info.FileId.Low & 0x0000_FFFF_FFFF_FFFF));
        }
    }

    /// <summary>
    /// With <see cref="ScanOptions.ResolveAllReparseTags"/>: reads the tags of nonresident reparse points inside the scanned
    /// tree (one cluster read each). Otherwise nothing is read and such nodes are flagged ReparseTagUnknown.
    /// Traversal does not depend on the tag here: in the MFT, a junction or symbolic link directory has no children of its
    /// own (its target is not its child), so skipping the tag never changes which nodes or sizes are found.
    /// </summary>
    private static int ResolveReachableReparseTags(
        MftVolume mft, MftTable table, (int[] Start, int[] Entries) children, int rootRecord, ScanOptions options, List<ScanError> errors)
    {
        if (!options.ResolveAllReparseTags || table.NonResidentReparse.Count == 0)
        {
            return 0;
        }

        var visited = new bool[table.RecordCount];
        var pending = new List<int>();
        var stack = new Stack<int>();
        stack.Push(rootRecord);
        visited[rootRecord] = true;
        if (table.NeedsReparseTag(rootRecord))
        {
            pending.Add(rootRecord);
        }

        while (stack.Count > 0)
        {
            int directory = stack.Pop();
            for (int i = children.Start[directory]; i < children.Start[directory + 1]; i++)
            {
                int record = table.NameRecord(children.Entries[i]);
                if ((directory == NtfsLayout.RootDirectoryRecord && record < NtfsLayout.FirstUserRecord) || visited[record])
                {
                    continue;
                }

                visited[record] = true;
                if (table.NeedsReparseTag(record))
                {
                    pending.Add(record);
                }

                if (table.IsDirectory(record))
                {
                    stack.Push(record);
                }
            }
        }

        // One cluster read each, every worker with its own volume handle.
        var files = pending;
        if (files.Count == 0)
        {
            return 0;
        }

        files.Sort();
        Parallel.ForEach(
            System.Collections.Concurrent.Partitioner.Create(0, files.Count, Math.Max(64, files.Count / 64)),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            mft.OpenReader,
            (range, _, handle) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    ResolveTag(mft, table, files[i], errors, handle);
                }

                return handle;
            },
            handle => handle.Dispose());
        return files.Count;
    }

    private static void ResolveTag(MftVolume mft, MftTable table, int record, List<ScanError> errors, Microsoft.Win32.SafeHandles.SafeFileHandle? handle = null)
    {
        var attribute = new NtfsRecord.Attribute(table.NonResidentReparse[record]);
        var extents = new List<Extent>();
        if (!attribute.DecodeMappingPairs(extents))
        {
            lock (errors)
            {
                errors.Add(new ScanError($"MFT record {record}", 0, "ReparseMappingPairs"));
            }

            return;
        }

        long length = Math.Min(attribute.FileSize, mft.ClusterSize);
        byte[] data = handle is null ? mft.ReadStream(extents, length) : mft.ReadStream(handle, extents, length);
        if (data.Length >= 4)
        {
            table.SetReparseTag(record, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data));
        }
    }

    /// <summary>CSR index: for each directory record, the name entries whose parent it is (with matching sequence).</summary>
    private static (int[] Start, int[] Entries) BuildChildIndex(MftTable table)
    {
        int records = table.RecordCount;
        var start = new int[records + 1];
        for (int e = 0; e < table.NameCount; e++)
        {
            if (IsValidLink(table, e))
            {
                start[table.NameParent(e) + 1]++;
            }
        }

        for (int r = 0; r < records; r++)
        {
            start[r + 1] += start[r];
        }

        var fill = (int[])start.Clone();
        var entries = new int[start[records]];
        for (int e = 0; e < table.NameCount; e++)
        {
            if (IsValidLink(table, e))
            {
                entries[fill[table.NameParent(e)]++] = e;
            }
        }

        return (start, entries);
    }

    private static bool IsValidLink(MftTable table, int entry)
    {
        int record = table.NameRecord(entry);
        int parent = table.NameParent(entry);
        return record != parent &&
               table.IsInUse(record) &&
               table.IsInUse(parent) &&
               table.IsDirectory(parent) &&
               table.Sequence(parent) == table.NameParentSequence(entry);
    }

    private static void BuildTree(
        MftTable table,
        (int[] Start, int[] Entries) children,
        ScanTreeBuilder builder,
        int rootNode,
        int rootRecord,
        ScanOptions options,
        ScanProgress progress,
        CancellationToken cancellationToken)
    {
        var visited = new bool[table.RecordCount];
        var stack = new Stack<(int Node, int Record)>();
        stack.Push((rootNode, rootRecord));
        visited[rootRecord] = true;

        var entries = new List<EntryData>();
        var names = new List<char>();
        var childRecords = new List<int>();
        var indices = new int[256];

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, directory) = stack.Pop();
            entries.Clear();
            names.Clear();
            childRecords.Clear();

            long files = 0, directories = 0, bytes = 0;
            for (int i = children.Start[directory]; i < children.Start[directory + 1]; i++)
            {
                int e = children.Entries[i];
                int record = table.NameRecord(e);

                // NTFS metadata files live directly in the root directory.
                if (directory == NtfsLayout.RootDirectoryRecord && record < NtfsLayout.FirstUserRecord)
                {
                    continue;
                }

                var data = table.ToEntry(record, options.IncludeAlternateStreams);
                var name = table.Name(e);
                data.NameStart = names.Count;
                data.NameLength = name.Length;
                foreach (char c in name)
                {
                    names.Add(c);
                }

                entries.Add(data);
                childRecords.Add(record);
                if ((data.Flags & NodeFlags.Directory) != 0)
                {
                    directories++;
                }
                else
                {
                    files++;
                }

                bytes += data.Allocated;
            }

            if (indices.Length < entries.Count)
            {
                indices = new int[entries.Count * 2];
            }

            builder.AddChildren(node, CollectionsMarshal.AsSpan(entries), CollectionsMarshal.AsSpan(names), indices);
            progress.Add(files, directories, bytes);

            for (int i = 0; i < entries.Count; i++)
            {
                int record = childRecords[i];
                if ((entries[i].Flags & NodeFlags.Directory) != 0 &&
                    !EntryClassifier.ShouldSkipDirectory(entries[i].Flags, options) &&
                    !visited[record])
                {
                    visited[record] = true;
                    stack.Push((indices[i], record));
                }
            }
        }
    }
}
