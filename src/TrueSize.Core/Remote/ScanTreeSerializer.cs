using System.Runtime.InteropServices;
using TrueSize.Core.Model;
using TrueSize.Core.Scanning;

namespace TrueSize.Core.Remote;

/// <summary>
/// Binary transfer of a scan result: raw struct-of-arrays as bytes, no per-node objects. Aggregates are not sent;
/// the receiver recomputes them (single pass). The reader validates every index before building the tree.
/// </summary>
internal static class ScanTreeSerializer
{
    private const ulong Magic = 0x3130_4545_5254_5354; // "TSTREE01" little-endian

    public static void Write(Stream stream, ScanResult result)
    {
        var tree = result.Tree;
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        WriteInfo(w, tree.Info);
        w.Write((byte)tree.HardlinkMode);
        w.Write(result.Duration.Ticks);

        int n = tree.Count;
        w.Write(n);
        WriteArray(w, tree.RawParent.AsSpan(0, n));
        WriteArray(w, tree.RawFirstChild.AsSpan(0, n));
        WriteArray(w, tree.RawNextSibling.AsSpan(0, n));
        WriteArray(w, tree.RawNameOffset.AsSpan(0, n));
        WriteArray(w, tree.RawNameLength.AsSpan(0, n));
        WriteArray(w, tree.RawFlags.AsSpan(0, n));
        WriteArray(w, tree.RawSize.AsSpan(0, n));
        WriteArray(w, tree.RawAllocated.AsSpan(0, n));
        WriteArray(w, tree.RawFileId.AsSpan(0, n));
        WriteArray(w, tree.RawLinkCount.AsSpan(0, n));

        w.Write(tree.RawNames.Length);
        WriteArray(w, tree.RawNames.AsSpan());

        // Bulk blocks only: BinaryWriter does not buffer, and every small write on an overlapped pipe is a full I/O
        // operation (1.3M reparse tags on C: took ~80 s as individual writes).
        var tags = tree.RawReparseTags;
        var tagNodes = new int[tags.Count];
        var tagValues = new uint[tags.Count];
        int t = 0;
        foreach (var (node, tag) in tags)
        {
            tagNodes[t] = node;
            tagValues[t] = tag;
            t++;
        }

        w.Write(tags.Count);
        WriteArray(w, tagNodes.AsSpan());
        WriteArray(w, tagValues.AsSpan());

        using var errorBlock = new MemoryStream();
        using (var ew = new BinaryWriter(errorBlock, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            ew.Write(result.Errors.Count);
            foreach (var error in result.Errors)
            {
                ew.Write(error.Path);
                ew.Write(error.Win32Error);
                ew.Write(error.Operation);
            }
        }

        w.Write(checked((int)errorBlock.Length));
        WriteArray(w, errorBlock.GetBuffer().AsSpan(0, (int)errorBlock.Length));
        w.Flush();
    }

    public static ScanResult Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt64() != Magic)
        {
            throw new InvalidDataException("Not a TrueSize scan result.");
        }

        var info = ReadInfo(r);
        var mode = (HardlinkMode)r.ReadByte();
        var duration = TimeSpan.FromTicks(r.ReadInt64());

        int n = r.ReadInt32();
        if (n < 1)
        {
            throw new InvalidDataException($"Invalid node count {n}.");
        }

        var parent = ReadArray<int>(r, n);
        var firstChild = ReadArray<int>(r, n);
        var nextSibling = ReadArray<int>(r, n);
        var nameOffset = ReadArray<int>(r, n);
        var nameLength = ReadArray<ushort>(r, n);
        var flags = ReadArray<NodeFlags>(r, n);
        var size = ReadArray<long>(r, n);
        var allocated = ReadArray<long>(r, n);
        var fileId = ReadArray<FileId128>(r, n);
        var linkCount = ReadArray<int>(r, n);

        int nameChars = r.ReadInt32();
        if (nameChars < 0)
        {
            throw new InvalidDataException("Invalid name pool length.");
        }

        var names = ReadArray<char>(r, nameChars);

        int tagCount = r.ReadInt32();
        if (tagCount < 0 || tagCount > n)
        {
            throw new InvalidDataException("Invalid reparse tag count.");
        }

        var tagNodes = ReadArray<int>(r, tagCount);
        var tagValues = ReadArray<uint>(r, tagCount);
        var tags = new Dictionary<int, uint>(tagCount);
        for (int i = 0; i < tagCount; i++)
        {
            if ((uint)tagNodes[i] >= (uint)n)
            {
                throw new InvalidDataException("Reparse tag for an invalid node.");
            }

            tags[tagNodes[i]] = tagValues[i];
        }

        int errorBytes = r.ReadInt32();
        if (errorBytes < 4)
        {
            throw new InvalidDataException("Invalid error block.");
        }

        using var errorReader = new BinaryReader(new MemoryStream(ReadArray<byte>(r, errorBytes), writable: false), System.Text.Encoding.UTF8);
        int errorCount = errorReader.ReadInt32();
        if (errorCount < 0)
        {
            throw new InvalidDataException("Invalid error count.");
        }

        var errors = new List<ScanError>(Math.Min(errorCount, 1 << 20));
        for (int i = 0; i < errorCount; i++)
        {
            errors.Add(new ScanError(errorReader.ReadString(), errorReader.ReadInt32(), errorReader.ReadString()));
        }

        Validate(n, parent, firstChild, nextSibling, nameOffset, nameLength, names.Length);

        var tree = new ScanTree(info, n, parent, firstChild, nextSibling, nameOffset, nameLength, flags, size, allocated, fileId, linkCount, names, tags);
        tree.Aggregate(Enum.IsDefined(mode) ? mode : HardlinkMode.FirstFound);
        return new ScanResult(tree, errors, duration);
    }

    // Aggregation relies on parent < child; child lists must stay inside the array.
    private static void Validate(int n, int[] parent, int[] firstChild, int[] nextSibling, int[] nameOffset, ushort[] nameLength, int nameChars)
    {
        if (parent[0] != -1)
        {
            throw new InvalidDataException("Root must not have a parent.");
        }

        for (int i = 0; i < n; i++)
        {
            if ((i > 0 && (parent[i] < 0 || parent[i] >= i)) ||
                firstChild[i] < -1 || firstChild[i] >= n ||
                nextSibling[i] < -1 || nextSibling[i] >= n ||
                nameOffset[i] < 0 || (long)nameOffset[i] + nameLength[i] > nameChars)
            {
                throw new InvalidDataException($"Corrupt node {i}.");
            }
        }
    }

    private static void WriteInfo(BinaryWriter w, ScanTreeInfo info)
    {
        w.Write(info.RootPath);
        w.Write(info.VolumeRoot);
        w.Write(info.VolumeSerial);
        w.Write(info.FileSystem);
        w.Write(info.Scanner);
        w.Write(info.LinkCountsExact);
        w.Write(info.AlternateStreamsIncluded);
        w.Write(info.Volume is not null);
        if (info.Volume is { } v)
        {
            w.Write(v.Root);
            w.Write(v.SerialNumber);
            w.Write(v.FileSystem);
            w.Write(v.TotalBytes);
            w.Write(v.FreeBytes);
        }
    }

    private static ScanTreeInfo ReadInfo(BinaryReader r)
    {
        string rootPath = r.ReadString();
        string volumeRoot = r.ReadString();
        uint serial = r.ReadUInt32();
        string fileSystem = r.ReadString();
        string scanner = r.ReadString();
        bool exact = r.ReadBoolean();
        bool streams = r.ReadBoolean();
        VolumeInfo? volume = r.ReadBoolean()
            ? new VolumeInfo(r.ReadString(), r.ReadUInt32(), r.ReadString(), r.ReadInt64(), r.ReadInt64())
            : null;
        return new ScanTreeInfo(rootPath, volumeRoot, serial, fileSystem, scanner, exact, streams, volume);
    }

    // Pipes opened for overlapped I/O copy every Read/Write through a temporary buffer as large as the span passed in.
    // Unbounded spans (hundreds of MB) made each call allocate and copy the whole remainder (quadratic cost); fixed-size
    // chunks keep each transfer at 1 MB.
    private const int ChunkBytes = 1024 * 1024;

    private static void WriteArray<T>(BinaryWriter w, ReadOnlySpan<T> values)
        where T : unmanaged
    {
        w.Flush();
        var bytes = MemoryMarshal.AsBytes(values);
        for (int offset = 0; offset < bytes.Length; offset += ChunkBytes)
        {
            w.BaseStream.Write(bytes.Slice(offset, Math.Min(ChunkBytes, bytes.Length - offset)));
        }
    }

    private static T[] ReadArray<T>(BinaryReader r, int count)
        where T : unmanaged
    {
        var result = GC.AllocateUninitializedArray<T>(count);
        var bytes = MemoryMarshal.AsBytes(result.AsSpan());
        for (int offset = 0; offset < bytes.Length; offset += ChunkBytes)
        {
            r.BaseStream.ReadExactly(bytes.Slice(offset, Math.Min(ChunkBytes, bytes.Length - offset)));
        }

        return result;
    }
}
