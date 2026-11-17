namespace TrueSize.Core.Model;

/// <summary>Raw per-entry data produced by a scanner before it is committed to the tree.</summary>
internal struct EntryData
{
    public NodeFlags Flags;
    public long Size;
    public long Allocated;
    public FileId128 FileId;

    /// <summary>Total hard link count, 0 = unknown.</summary>
    public int LinkCount;
    public uint ReparseTag;
    public int NameStart;
    public int NameLength;
}

/// <summary>
/// Thread-safe tree builder. Scanners commit one directory listing per call, so the lock is taken
/// once per directory, not per file. Node indices of children are always greater than their parent's.
/// </summary>
internal sealed class ScanTreeBuilder
{
    private const int NameChunkSize = 1 << 20;

    private readonly Lock _gate = new();
    private readonly ChunkedArray<int> _parent = new();
    private readonly ChunkedArray<int> _firstChild = new();
    private readonly ChunkedArray<int> _nextSibling = new();
    private readonly ChunkedArray<int> _nameOffset = new();
    private readonly ChunkedArray<ushort> _nameLength = new();
    private readonly ChunkedArray<NodeFlags> _flags = new();
    private readonly ChunkedArray<long> _size = new();
    private readonly ChunkedArray<long> _allocated = new();
    private readonly ChunkedArray<FileId128> _fileId = new();
    private readonly ChunkedArray<int> _linkCount = new();
    private readonly List<char[]> _nameChunks = [new char[NameChunkSize]];
    private readonly Dictionary<int, uint> _reparseTags = [];
    private readonly List<int> _closedChunkUsed = [];
    private int _nameChunkUsed;
    private int _count;

    public int Count => _count;

    public int AddRoot(string name, in EntryData data)
    {
        EntryData root = data;
        root.Flags |= NodeFlags.Directory;
        root.NameStart = 0;
        root.NameLength = name.Length;
        Span<int> index = stackalloc int[1];
        AddChildren(-1, new ReadOnlySpan<EntryData>(in root), name, index);
        return index[0];
    }

    /// <summary>Appends entries as children of <paramref name="parent"/> and returns their node indices.</summary>
    public void AddChildren(int parent, ReadOnlySpan<EntryData> entries, ReadOnlySpan<char> names, Span<int> indices)
    {
        lock (_gate)
        {
            int start = _count;
            int end = checked(start + entries.Length);
            EnsureCapacity(end);

            for (int i = 0; i < entries.Length; i++)
            {
                ref readonly EntryData e = ref entries[i];
                int node = start + i;
                indices[i] = node;

                _parent[node] = parent;
                _firstChild[node] = -1;
                _flags[node] = e.Flags;
                _size[node] = e.Size;
                _allocated[node] = e.Allocated;
                _fileId[node] = e.FileId;
                _linkCount[node] = e.LinkCount;
                if (e.ReparseTag != 0)
                {
                    _reparseTags[node] = e.ReparseTag;
                }

                _nameOffset[node] = AppendName(names.Slice(e.NameStart, e.NameLength));
                _nameLength[node] = checked((ushort)e.NameLength);

                // Prepend to the parent's child list.
                if (parent >= 0)
                {
                    _nextSibling[node] = _firstChild[parent];
                    _firstChild[parent] = node;
                }
                else
                {
                    _nextSibling[node] = -1;
                }
            }

            _count = end;
        }
    }

    public void SetAllocated(int node, long allocated)
    {
        lock (_gate)
        {
            _allocated[node] = allocated;
        }
    }

    public void SetFlags(int node, NodeFlags flags)
    {
        lock (_gate)
        {
            _flags[node] |= flags;
        }
    }

    private void EnsureCapacity(int capacity)
    {
        _parent.EnsureCapacity(capacity);
        _firstChild.EnsureCapacity(capacity);
        _nextSibling.EnsureCapacity(capacity);
        _nameOffset.EnsureCapacity(capacity);
        _nameLength.EnsureCapacity(capacity);
        _flags.EnsureCapacity(capacity);
        _size.EnsureCapacity(capacity);
        _allocated.EnsureCapacity(capacity);
        _fileId.EnsureCapacity(capacity);
        _linkCount.EnsureCapacity(capacity);
    }

    private int AppendName(ReadOnlySpan<char> name)
    {
        if (_nameChunkUsed + name.Length > NameChunkSize)
        {
            _closedChunkUsed.Add(_nameChunkUsed);
            _nameChunks.Add(new char[NameChunkSize]);
            _nameChunkUsed = 0;
        }

        int offset = _nameChunkUsed;
        name.CopyTo(_nameChunks[^1].AsSpan(offset));
        _nameChunkUsed += name.Length;
        return checked(((_nameChunks.Count - 1) * NameChunkSize) + offset);
    }

    public ScanTree Build(ScanTreeInfo info)
    {
        lock (_gate)
        {
            int n = _count;

            // Flatten the name pool. Chunks are concatenated without their unused tails, so offsets are rebased.
            int chunks = _nameChunks.Count;
            int[] chunkBase = new int[chunks];
            long total = 0;
            for (int c = 0; c < chunks; c++)
            {
                chunkBase[c] = checked((int)total);
                total += UsedLength(c);
            }

            char[] names = GC.AllocateUninitializedArray<char>(checked((int)total));
            for (int c = 0; c < chunks; c++)
            {
                _nameChunks[c].AsSpan(0, UsedLength(c)).CopyTo(names.AsSpan(chunkBase[c]));
            }

            int[] nameOffset = _nameOffset.ToArrayAndRelease(n);
            for (int i = 0; i < n; i++)
            {
                int raw = nameOffset[i];
                nameOffset[i] = chunkBase[raw / NameChunkSize] + (raw % NameChunkSize);
            }

            _nameChunks.Clear();

            return new ScanTree(
                info,
                n,
                _parent.ToArrayAndRelease(n),
                _firstChild.ToArrayAndRelease(n),
                _nextSibling.ToArrayAndRelease(n),
                nameOffset,
                _nameLength.ToArrayAndRelease(n),
                _flags.ToArrayAndRelease(n),
                _size.ToArrayAndRelease(n),
                _allocated.ToArrayAndRelease(n),
                _fileId.ToArrayAndRelease(n),
                _linkCount.ToArrayAndRelease(n),
                names,
                new Dictionary<int, uint>(_reparseTags));
        }
    }

    // A chunk is closed as soon as a name does not fit; its unused tail is never written.
    private int UsedLength(int chunk) => chunk == _nameChunks.Count - 1 ? _nameChunkUsed : _closedChunkUsed[chunk];
}
