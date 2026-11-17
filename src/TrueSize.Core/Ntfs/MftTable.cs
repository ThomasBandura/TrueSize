using System.Buffers.Binary;
using TrueSize.Core.Interop;
using TrueSize.Core.Model;

namespace TrueSize.Core.Ntfs;

/// <summary>
/// Per-record facts collected from one pass over the MFT. Extension records are merged into their base record
/// via BaseFileRecordSegment, so $ATTRIBUTE_LIST does not need to be read for regular files.
/// </summary>
internal sealed class MftTable
{
    private const byte StateInUse = 1;
    private const byte StateDirectory = 2;
    private const byte StateAlternateStreams = 4;
    private const byte StateReparseNonResident = 8;

    private readonly byte[] _state;
    private readonly ushort[] _sequence;
    private readonly long[] _size;
    private readonly long[] _allocated;
    private readonly long[] _indexAllocated;
    private readonly ushort[] _dataFlags;
    private readonly uint[] _reparseTag;
    private readonly int[] _linkCount;
    private readonly Dictionary<int, (long Size, long Allocated)> _alternate = [];
    private readonly Dictionary<int, long> _wofAllocated = [];

    // Name entries (one per hard link), appended in MFT order.
    private int[] _nameRecord = new int[1024];
    private int[] _nameParent = new int[1024];
    private ushort[] _nameParentSequence = new ushort[1024];
    private int[] _nameOffset = new int[1024];
    private byte[] _nameLength = new byte[1024];
    private char[] _names = new char[1 << 16];
    private int _namesUsed;

    public MftTable(long recordCount)
    {
        int n = checked((int)recordCount);
        RecordCount = n;
        _state = new byte[n];
        _sequence = new ushort[n];
        _size = new long[n];
        _allocated = new long[n];
        _indexAllocated = new long[n];
        _dataFlags = new ushort[n];
        _reparseTag = new uint[n];
        _linkCount = new int[n];
    }

    public int RecordCount { get; }

    public int NameCount { get; private set; }

    public List<(int Record, long Segment)> InvalidRecords { get; } = [];

    /// <summary>
    /// Nonresident $REPARSE_POINT attributes (e.g. cloud placeholders with large reparse data). The tag needs one cluster
    /// read each, so it is resolved only for records inside the scanned tree.
    /// </summary>
    public Dictionary<int, byte[]> NonResidentReparse { get; } = [];

    public bool NeedsReparseTag(int record) => (_state[record] & StateReparseNonResident) != 0 && _reparseTag[record] == 0;

    /// <summary>Reparse tags copied in $FILE_NAME (undocumented field, see NtfsLayout), keyed by base record.</summary>
    private readonly Dictionary<int, uint> _fileNameReparseTags = [];

    /// <summary>Records whose $FILE_NAME tag disagrees with a resident $REPARSE_POINT (self-check; expected 0).</summary>
    public int FileNameTagMismatches { get; private set; }

    /// <summary>
    /// After the pass: use the $FILE_NAME copy of the tag for nonresident reparse points (avoids a cluster read each)
    /// and cross-check it against resident tags.
    /// </summary>
    public int ApplyFileNameReparseTags()
    {
        int applied = 0;
        foreach (var (record, tag) in _fileNameReparseTags)
        {
            if (NeedsReparseTag(record))
            {
                _reparseTag[record] = tag;
                applied++;
            }
            else if ((_state[record] & StateReparseNonResident) == 0 && _reparseTag[record] != 0 && _reparseTag[record] != tag)
            {
                FileNameTagMismatches++;
            }
        }

        return applied;
    }

    public bool IsInUse(int record) => (_state[record] & StateInUse) != 0;

    public bool IsDirectory(int record) => (_state[record] & StateDirectory) != 0;

    public ushort Sequence(int record) => _sequence[record];

    public int LinkCount(int record) => _linkCount[record];

    public void SetReparseTag(int record, uint tag) => _reparseTag[record] = tag;

    public int NameRecord(int entry) => _nameRecord[entry];

    public int NameParent(int entry) => _nameParent[entry];

    public ushort NameParentSequence(int entry) => _nameParentSequence[entry];

    public ReadOnlySpan<char> Name(int entry) => _names.AsSpan(_nameOffset[entry], _nameLength[entry]);

    /// <summary>Parses one record (fixups not yet applied).</summary>
    public void Add(long recordNumber, Span<byte> record)
    {
        int r = (int)recordNumber;
        if (!NtfsRecord.HasFileSignature(record))
        {
            return;
        }

        if (!NtfsRecord.ApplyFixup(record))
        {
            InvalidRecords.Add((r, recordNumber));
            return;
        }

        ushort flags = NtfsRecord.Flags(record);
        if ((flags & NtfsLayout.FILE_RECORD_SEGMENT_IN_USE) == 0)
        {
            return;
        }

        long baseSegment = NtfsRecord.BaseSegment(record);
        int target = baseSegment == 0 ? r : (int)baseSegment;
        if ((uint)target >= (uint)RecordCount)
        {
            return;
        }

        if (baseSegment == 0)
        {
            _state[r] |= StateInUse;
            _sequence[r] = NtfsRecord.SequenceNumber(record);
            if ((flags & NtfsLayout.FILE_FILE_NAME_INDEX_PRESENT) != 0)
            {
                _state[r] |= StateDirectory;
            }
        }

        foreach (var attribute in NtfsRecord.Attributes(record))
        {
            switch (attribute.TypeCode)
            {
                case NtfsLayout.FILE_NAME:
                    AddName(target, attribute);
                    break;
                case NtfsLayout.DATA:
                    AddData(target, attribute);
                    break;
                case NtfsLayout.INDEX_ALLOCATION:
                    if (!attribute.IsResident && attribute.LowestVcn == 0 && attribute.Name.SequenceEqual("$I30"))
                    {
                        _indexAllocated[target] = attribute.AllocatedLength;
                    }

                    break;
                case NtfsLayout.REPARSE_POINT:
                    if (attribute.IsResident)
                    {
                        // The reparse data buffer starts with DWORD ReparseTag (winnt.h REPARSE_GUID_DATA_BUFFER).
                        var value = attribute.Value;
                        if (value.Length >= 4)
                        {
                            _reparseTag[target] = BinaryPrimitives.ReadUInt32LittleEndian(value);
                        }
                    }
                    else
                    {
                        _state[target] |= StateReparseNonResident;
                        NonResidentReparse[target] = attribute.Span.ToArray();
                    }

                    break;
            }
        }
    }

    private void AddName(int target, NtfsRecord.Attribute attribute)
    {
        var value = attribute.Value;
        if (value.Length < NtfsLayout.FileNameChars)
        {
            return;
        }

        byte nameFlags = value[NtfsLayout.FileNameFlags];

        // A name only in the DOS (8.3) namespace is an alias of another name, not a hard link.
        if (nameFlags == NtfsLayout.FILE_NAME_DOS)
        {
            return;
        }

        int length = value[NtfsLayout.FileNameLength];
        if (NtfsLayout.FileNameChars + (length * sizeof(char)) > value.Length)
        {
            return;
        }

        var (parent, parentSequence) = NtfsLayout.ReadSegmentReference(value[NtfsLayout.FileNameParent..]);
        if (parent >= RecordCount)
        {
            return;
        }

        uint copiedAttributes = BinaryPrimitives.ReadUInt32LittleEndian(value[NtfsLayout.FileNameAttributesUndocumented..]);
        if ((copiedAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(value[NtfsLayout.FileNameReparseTagUndocumented..]);
            if (tag != 0)
            {
                _fileNameReparseTags[target] = tag;
            }
        }

        EnsureNameCapacity(length);
        int entry = NameCount++;
        _nameRecord[entry] = target;
        _nameParent[entry] = (int)parent;
        _nameParentSequence[entry] = parentSequence;
        _nameOffset[entry] = _namesUsed;
        _nameLength[entry] = (byte)length;
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(value.Slice(NtfsLayout.FileNameChars, length * sizeof(char)))
            .CopyTo(_names.AsSpan(_namesUsed));
        _namesUsed += length;
        _linkCount[target]++;
    }

    private void AddData(int target, NtfsRecord.Attribute attribute)
    {
        // Sizes are only valid in the attribute record with LowestVcn 0.
        if (attribute.LowestVcn != 0)
        {
            return;
        }

        var name = attribute.Name;
        long size = attribute.FileSize;
        long allocated = attribute.IsResident ? ResidentAllocated(size) : attribute.ActualAllocated;
        if (name.IsEmpty)
        {
            _size[target] = size;
            _allocated[target] = allocated;
            _dataFlags[target] = attribute.Flags;
        }
        else if (name.SequenceEqual("WofCompressedData"))
        {
            _wofAllocated[target] = allocated;
        }
        else
        {
            _state[target] |= StateAlternateStreams;
            _alternate.TryGetValue(target, out var sum);
            _alternate[target] = (sum.Size + size, sum.Allocated + allocated);
        }
    }

    /// <summary>
    /// Allocation reported for resident data: FileStandardInfo.AllocationSize returns the value length rounded up to
    /// 8 bytes (measured: 404 -> 408, 477 -> 480 on C:\Program Files\dotnet). Not documented; covered by NtfsMftScannerTests.
    /// </summary>
    internal static long ResidentAllocated(long valueLength) => (valueLength + 7) & ~7L;

    /// <summary>Builds the node data for one name entry of a record.</summary>
    public EntryData ToEntry(int record, bool includeAlternateStreams)
    {
        bool isDirectory = IsDirectory(record);
        ushort dataFlags = _dataFlags[record];
        uint tag = _reparseTag[record];

        uint attributes = 0;
        if (isDirectory)
        {
            attributes |= Native.FILE_ATTRIBUTE_DIRECTORY;
        }

        if ((dataFlags & NtfsLayout.ATTRIBUTE_FLAG_SPARSE) != 0)
        {
            attributes |= Native.FILE_ATTRIBUTE_SPARSE_FILE;
        }

        if ((dataFlags & NtfsLayout.ATTRIBUTE_FLAG_COMPRESSION_MASK) != 0)
        {
            attributes |= Native.FILE_ATTRIBUTE_COMPRESSED;
        }

        if ((dataFlags & NtfsLayout.ATTRIBUTE_FLAG_ENCRYPTED) != 0)
        {
            attributes |= Native.FILE_ATTRIBUTE_ENCRYPTED;
        }

        if (tag != 0 || (_state[record] & StateReparseNonResident) != 0)
        {
            attributes |= Native.FILE_ATTRIBUTE_REPARSE_POINT;
        }

        var entry = new EntryData
        {
            Flags = Scanning.EntryClassifier.Classify(attributes, tag),
            ReparseTag = tag,
            FileId = new FileId128((uint)record | ((ulong)_sequence[record] << 48), 0),
            LinkCount = isDirectory ? 0 : _linkCount[record],
            Size = isDirectory ? 0 : _size[record],
            Allocated = isDirectory ? _indexAllocated[record] : _allocated[record],
        };

        if (NeedsReparseTag(record))
        {
            entry.Flags |= NodeFlags.ReparseTagUnknown;
        }

        if (_wofAllocated.TryGetValue(record, out long wof))
        {
            entry.Allocated += wof;
        }

        if (includeAlternateStreams && _alternate.TryGetValue(record, out var alt))
        {
            entry.Size += alt.Size;
            entry.Allocated += alt.Allocated;
            entry.Flags |= NodeFlags.AlternateStreams;
        }

        return entry;
    }

    private void EnsureNameCapacity(int length)
    {
        if (NameCount == _nameRecord.Length)
        {
            int n = _nameRecord.Length * 2;
            Array.Resize(ref _nameRecord, n);
            Array.Resize(ref _nameParent, n);
            Array.Resize(ref _nameParentSequence, n);
            Array.Resize(ref _nameOffset, n);
            Array.Resize(ref _nameLength, n);
        }

        if (_namesUsed + length > _names.Length)
        {
            Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesUsed + length));
        }
    }
}
