namespace TrueSize.Core.Model;

public sealed record ScanTreeInfo(
    string RootPath,
    string VolumeRoot,
    uint VolumeSerial,
    string FileSystem,
    string Scanner,
    bool LinkCountsExact,
    bool AlternateStreamsIncluded,
    Scanning.VolumeInfo? Volume = null)
{
    /// <summary>True if the scan covered the whole volume (root path is the volume root).</summary>
    public bool IsVolumeRoot => string.Equals(
        RootPath.TrimEnd('\\'), VolumeRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Read-only snapshot of one node with its aggregated values.</summary>
public readonly record struct NodeView(
    int Index,
    string Name,
    NodeFlags Flags,
    long Size,
    long Allocated,
    long TotalSize,
    long TotalAllocated,
    long Freeable,
    long HardlinkSize,
    long HardlinkAllocated,
    long FileCount,
    long DirectoryCount,
    int LinkCount)
{
    public bool IsDirectory => (Flags & NodeFlags.Directory) != 0;
}

/// <summary>
/// Struct-of-arrays scan result. Node 0 is the scan root; every child index is greater than its parent index,
/// which allows aggregation in a single reverse pass.
/// </summary>
public sealed class ScanTree
{
    private readonly int[] _parent;
    private readonly int[] _firstChild;
    private readonly int[] _nextSibling;
    private readonly int[] _nameOffset;
    private readonly ushort[] _nameLength;
    private readonly NodeFlags[] _flags;
    private readonly long[] _size;
    private readonly long[] _allocated;
    private readonly FileId128[] _fileId;
    private readonly int[] _linkCount;
    private readonly char[] _names;
    private readonly Dictionary<int, uint> _reparseTags;

    private long[] _totalSize = [];
    private long[] _totalAllocated = [];
    private long[] _freeable = [];
    private long[] _hardlinkSize = [];
    private long[] _hardlinkAllocated = [];
    private long[] _fileCount = [];
    private long[] _directoryCount = [];
    private int[]? _depth;

    internal ScanTree(
        ScanTreeInfo info,
        int count,
        int[] parent,
        int[] firstChild,
        int[] nextSibling,
        int[] nameOffset,
        ushort[] nameLength,
        NodeFlags[] flags,
        long[] size,
        long[] allocated,
        FileId128[] fileId,
        int[] linkCount,
        char[] names,
        Dictionary<int, uint> reparseTags)
    {
        Info = info;
        Count = count;
        _parent = parent;
        _firstChild = firstChild;
        _nextSibling = nextSibling;
        _nameOffset = nameOffset;
        _nameLength = nameLength;
        _flags = flags;
        _size = size;
        _allocated = allocated;
        _fileId = fileId;
        _linkCount = linkCount;
        _names = names;
        _reparseTags = reparseTags;
    }

    public const int Root = 0;

    public ScanTreeInfo Info { get; }

    public int Count { get; }

    public HardlinkMode HardlinkMode { get; private set; }

    public ReadOnlySpan<char> GetName(int node) => _names.AsSpan(_nameOffset[node], _nameLength[node]);

    public int GetParent(int node) => _parent[node];

    public int GetFirstChild(int node) => _firstChild[node];

    public int GetNextSibling(int node) => _nextSibling[node];

    public NodeFlags GetFlags(int node) => _flags[node];

    /// <summary>Own logical size (all included streams) of the node itself.</summary>
    public long GetSize(int node) => _size[node];

    /// <summary>Own allocated size of the node itself.</summary>
    public long GetAllocated(int node) => _allocated[node];

    public int GetLinkCount(int node) => _linkCount[node];

    public FileId128 GetFileId(int node) => _fileId[node];

    public bool TryGetReparseTag(int node, out uint tag) => _reparseTags.TryGetValue(node, out tag);

    public long GetTotalSize(int node) => _totalSize[node];

    public long GetTotalAllocated(int node) => _totalAllocated[node];

    /// <summary>Allocated bytes that would be released by deleting this subtree.</summary>
    public long GetFreeable(int node) => _freeable[node];

    public long GetHardlinkSize(int node) => _hardlinkSize[node];

    public long GetHardlinkAllocated(int node) => _hardlinkAllocated[node];

    public long GetFileCount(int node) => _fileCount[node];

    public long GetDirectoryCount(int node) => _directoryCount[node];

    public NodeView this[int node] => new(
        node,
        GetName(node).ToString(),
        _flags[node],
        _size[node],
        _allocated[node],
        _totalSize[node],
        _totalAllocated[node],
        _freeable[node],
        _hardlinkSize[node],
        _hardlinkAllocated[node],
        _fileCount[node],
        _directoryCount[node],
        _linkCount[node]);

    public ChildEnumerable Children(int node) => new(this, node);

    /// <summary>
    /// Lower bound of allocated bytes that are shared between files (ReFS block cloning or deduplication) and therefore
    /// counted more than once in Allocated. Only determinable for a whole-volume scan: if the per-file allocation exceeds
    /// the space actually in use, the excess must be shared. Null when not determinable; 0 does not prove "no sharing",
    /// because file system metadata can hide it. Never attributed to individual files.
    /// </summary>
    public long? MinimumSharedAllocation
    {
        get
        {
            if (Info.Volume is not { CanShareClusters: true, TotalBytes: > 0 } volume || !Info.IsVolumeRoot)
            {
                return null;
            }

            return Math.Max(0, GetTotalAllocated(Root) - volume.UsedBytes);
        }
    }

    public string GetPath(int node)
    {
        if (node == Root)
        {
            return GetName(Root).ToString();
        }

        Span<int> stack = stackalloc int[256];
        int depth = 0;
        int length = 0;
        for (int n = node; n != Root; n = _parent[n])
        {
            if (depth == stack.Length)
            {
                var bigger = new int[stack.Length * 2];
                stack.CopyTo(bigger);
                stack = bigger;
            }

            stack[depth++] = n;
            length += _nameLength[n] + 1;
        }

        var root = GetName(Root);
        bool rootHasSeparator = root.Length > 0 && root[^1] == '\\';
        return string.Create(root.Length + length - (rootHasSeparator ? 1 : 0), (tree: this, stack: stack[..depth].ToArray(), rootHasSeparator), static (span, s) =>
        {
            var r = s.tree.GetName(Root);
            r.CopyTo(span);
            int pos = r.Length;
            for (int i = s.stack.Length - 1; i >= 0; i--)
            {
                if (!(pos == r.Length && s.rootHasSeparator))
                {
                    span[pos++] = '\\';
                }

                var name = s.tree.GetName(s.stack[i]);
                name.CopyTo(span[pos..]);
                pos += name.Length;
            }
        });
    }

    /// <summary>Finds a node by a path relative to the root (case-insensitive). Returns -1 if not found.</summary>
    public int Find(string relativePath)
    {
        int node = Root;
        foreach (var part in relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            int match = -1;
            for (int c = _firstChild[node]; c >= 0; c = _nextSibling[c])
            {
                if (GetName(c).Equals(part, StringComparison.OrdinalIgnoreCase))
                {
                    match = c;
                    break;
                }
            }

            if (match < 0)
            {
                return -1;
            }

            node = match;
        }

        return node;
    }

    /// <summary>Computes all aggregated values. Can be called again with a different mode without rescanning.</summary>
    public void Aggregate(HardlinkMode mode)
    {
        int n = Count;
        var totalSize = new long[n];
        var totalAllocated = new long[n];
        var freeable = new long[n];
        var hardlinkSize = new long[n];
        var hardlinkAllocated = new long[n];
        var fileCount = new long[n];
        var directoryCount = new long[n];

        const NodeFlags hardlinkFlags = NodeFlags.Hardlink | NodeFlags.ExternalLinks | NodeFlags.HardlinkSecondary;
        int candidateCount = 0;

        for (int i = 0; i < n; i++)
        {
            _flags[i] &= ~hardlinkFlags;
            if ((_flags[i] & NodeFlags.Directory) == 0)
            {
                fileCount[i] = 1;
                if (IsHardlinkCandidate(i))
                {
                    candidateCount++;
                    continue;
                }
            }

            totalSize[i] = _size[i];
            totalAllocated[i] = _allocated[i];
            freeable[i] = _allocated[i];
        }

        if (candidateCount > 0)
        {
            ResolveHardlinks(mode, candidateCount, totalSize, totalAllocated, freeable, hardlinkSize, hardlinkAllocated);
        }

        for (int i = n - 1; i > Root; i--)
        {
            int p = _parent[i];
            totalSize[p] += totalSize[i];
            totalAllocated[p] += totalAllocated[i];
            freeable[p] += freeable[i];
            hardlinkSize[p] += hardlinkSize[i];
            hardlinkAllocated[p] += hardlinkAllocated[i];
            fileCount[p] += fileCount[i];
            if ((_flags[i] & NodeFlags.Directory) != 0)
            {
                directoryCount[p] += directoryCount[i] + 1;
            }
        }

        _totalSize = totalSize;
        _totalAllocated = totalAllocated;
        _freeable = freeable;
        _hardlinkSize = hardlinkSize;
        _hardlinkAllocated = hardlinkAllocated;
        _fileCount = fileCount;
        _directoryCount = directoryCount;
        HardlinkMode = mode;
    }

    // Known link count > 1, or unknown link count (then duplicates can only be detected by FileId within scope).
    private bool IsHardlinkCandidate(int i) => _linkCount[i] > 1 || (_linkCount[i] == 0 && !_fileId[i].IsZero);

    private void ResolveHardlinks(
        HardlinkMode mode,
        int candidateCount,
        long[] totalSize,
        long[] totalAllocated,
        long[] freeable,
        long[] hardlinkSize,
        long[] hardlinkAllocated)
    {
        var keys = new FileId128[candidateCount];
        var nodes = new int[candidateCount];
        int k = 0;
        for (int i = 0; i < Count; i++)
        {
            if ((_flags[i] & NodeFlags.Directory) == 0 && IsHardlinkCandidate(i))
            {
                keys[k] = _fileId[i];
                nodes[k] = i;
                k++;
            }
        }

        Array.Sort(keys, nodes);

        for (int start = 0; start < candidateCount;)
        {
            int end = start + 1;
            while (end < candidateCount && keys[end] == keys[start])
            {
                end++;
            }

            ResolveGroup(mode, nodes.AsSpan(start, end - start), totalSize, totalAllocated, freeable, hardlinkSize, hardlinkAllocated);
            start = end;
        }
    }

    private void ResolveGroup(
        HardlinkMode mode,
        Span<int> group,
        long[] totalSize,
        long[] totalAllocated,
        long[] freeable,
        long[] hardlinkSize,
        long[] hardlinkAllocated)
    {
        int inScope = group.Length;
        int linkCount = 0;
        foreach (int node in group)
        {
            linkCount = Math.Max(linkCount, _linkCount[node]);
        }

        // Unknown link count and only one occurrence: treat as a regular file.
        if (linkCount == 0 && inScope == 1)
        {
            int node = group[0];
            totalSize[node] = _size[node];
            totalAllocated[node] = _allocated[node];
            freeable[node] = _allocated[node];
            return;
        }

        int effectiveLinks = linkCount == 0 ? inScope : linkCount;
        bool external = inScope < effectiveLinks;

        // Deterministic primary: ordinally smallest path (scan order is non-deterministic with parallelism).
        int primary = group[0];
        if (inScope > 1)
        {
            string best = GetPath(primary);
            for (int g = 1; g < inScope; g++)
            {
                string path = GetPath(group[g]);
                if (string.CompareOrdinal(path, best) < 0)
                {
                    best = path;
                    primary = group[g];
                }
            }
        }

        long size = _size[primary];
        long allocated = _allocated[primary];

        foreach (int node in group)
        {
            _flags[node] |= NodeFlags.Hardlink;
            if (external)
            {
                _flags[node] |= NodeFlags.ExternalLinks;
            }

            if (node != primary)
            {
                _flags[node] |= NodeFlags.HardlinkSecondary;
            }

            switch (mode)
            {
                case HardlinkMode.FirstFound:
                    totalSize[node] = node == primary ? size : 0;
                    totalAllocated[node] = node == primary ? allocated : 0;
                    break;
                case HardlinkMode.Proportional:
                    totalSize[node] = (size / inScope) + (node == primary ? size % inScope : 0);
                    totalAllocated[node] = (allocated / inScope) + (node == primary ? allocated % inScope : 0);
                    break;
                case HardlinkMode.Separate:
                    totalSize[node] = 0;
                    totalAllocated[node] = 0;
                    hardlinkSize[node] = node == primary ? size : 0;
                    hardlinkAllocated[node] = node == primary ? allocated : 0;
                    break;
            }
        }

        // Space is only released when every link is deleted: credit it to the lowest common ancestor.
        if (!external)
        {
            int lca = group[0];
            for (int g = 1; g < inScope; g++)
            {
                lca = LowestCommonAncestor(lca, group[g]);
            }

            freeable[lca] += allocated;
        }
    }

    private int LowestCommonAncestor(int a, int b)
    {
        var depth = _depth ??= ComputeDepth();
        while (depth[a] > depth[b])
        {
            a = _parent[a];
        }

        while (depth[b] > depth[a])
        {
            b = _parent[b];
        }

        while (a != b)
        {
            a = _parent[a];
            b = _parent[b];
        }

        return a;
    }

    private int[] ComputeDepth()
    {
        var depth = new int[Count];
        for (int i = 1; i < Count; i++)
        {
            depth[i] = depth[_parent[i]] + 1;
        }

        return depth;
    }

    public readonly struct ChildEnumerable(ScanTree tree, int node)
    {
        public ChildEnumerator GetEnumerator() => new(tree, tree._firstChild[node]);
    }

    public struct ChildEnumerator(ScanTree tree, int first)
    {
        private int _next = first;

        public int Current { get; private set; } = -1;

        public bool MoveNext()
        {
            if (_next < 0)
            {
                return false;
            }

            Current = _next;
            _next = tree._nextSibling[_next];
            return true;
        }
    }
}
