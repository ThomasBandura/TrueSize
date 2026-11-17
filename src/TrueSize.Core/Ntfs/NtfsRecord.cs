using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TrueSize.Core.Ntfs;

/// <summary>A run of clusters: VCN range [Vcn, Vcn + Length) maps to LCN Lcn, or is unallocated (Lcn &lt; 0).</summary>
internal readonly record struct Extent(long Vcn, long Lcn, long Length);

/// <summary>Stateless helpers for one MFT file record segment.</summary>
internal static class NtfsRecord
{
    /// <summary>
    /// Validates the update sequence array and restores the saved words (MULTI_SECTOR_HEADER remarks):
    /// the last USHORT of every 512-byte stride must equal the update sequence number; it is replaced by the saved value.
    /// </summary>
    public static bool ApplyFixup(Span<byte> record)
    {
        int strides = record.Length / NtfsLayout.SequenceStride;
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecordUsaOffset..]);
        int usaSize = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecordUsaSize..]);

        // The documentation states the size "in bytes"; accept both a byte count and a USHORT count (strides + 1)
        // and always use the stride count derived from the record size.
        if (usaSize != strides + 1 && usaSize != (strides + 1) * sizeof(ushort))
        {
            return false;
        }

        if (usaOffset < 8 || usaOffset + ((strides + 1) * sizeof(ushort)) > NtfsLayout.SequenceStride - sizeof(ushort))
        {
            return false;
        }

        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i <= strides; i++)
        {
            int end = (i * NtfsLayout.SequenceStride) - sizeof(ushort);
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != usn)
            {
                return false;
            }

            record.Slice(usaOffset + (i * sizeof(ushort)), sizeof(ushort)).CopyTo(record.Slice(end, sizeof(ushort)));
        }

        return true;
    }

    public static bool HasFileSignature(ReadOnlySpan<byte> record) =>
        record.Length >= 48 && BinaryPrimitives.ReadUInt32LittleEndian(record) == NtfsLayout.FileSignature;

    public static ushort Flags(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecordFlags..]);

    public static ushort SequenceNumber(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecordSequenceNumber..]);

    public static long BaseSegment(ReadOnlySpan<byte> record) => NtfsLayout.ReadSegmentReference(record[NtfsLayout.RecordBaseFileRecordSegment..]).Segment;

    public static AttributeEnumerator Attributes(ReadOnlySpan<byte> record) => new(record);

    /// <summary>Iterates attribute records until $END or the end of the record.</summary>
    public ref struct AttributeEnumerator
    {
        private readonly ReadOnlySpan<byte> _record;
        private int _offset;
        private int _next;

        public AttributeEnumerator(ReadOnlySpan<byte> record)
        {
            _record = record;
            _next = BinaryPrimitives.ReadUInt16LittleEndian(record[NtfsLayout.RecordFirstAttributeOffset..]);
            _offset = -1;
        }

        public readonly Attribute Current => new(_record[_offset..(_offset + BinaryPrimitives.ReadInt32LittleEndian(_record[(_offset + NtfsLayout.AttrRecordLength)..]))]);

        public readonly AttributeEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            int offset = _next;
            if (offset <= 0 || offset + 16 > _record.Length || (offset & 7) != 0)
            {
                return false;
            }

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(_record[offset..]);
            if (type == NtfsLayout.END)
            {
                return false;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(_record[(offset + NtfsLayout.AttrRecordLength)..]);
            if (length < 16 || offset + length > _record.Length)
            {
                return false;
            }

            _offset = offset;
            _next = offset + length;
            return true;
        }
    }

    /// <summary>View over one ATTRIBUTE_RECORD_HEADER and its value.</summary>
    public readonly ref struct Attribute(ReadOnlySpan<byte> span)
    {
        public ReadOnlySpan<byte> Span { get; } = span;

        public uint TypeCode => BinaryPrimitives.ReadUInt32LittleEndian(Span);

        public bool IsResident => Span[NtfsLayout.AttrFormCode] == NtfsLayout.RESIDENT_FORM;

        public ushort Flags => BinaryPrimitives.ReadUInt16LittleEndian(Span[NtfsLayout.AttrFlags..]);

        public ReadOnlySpan<char> Name
        {
            get
            {
                int length = Span[NtfsLayout.AttrNameLength];
                if (length == 0)
                {
                    return default;
                }

                int offset = BinaryPrimitives.ReadUInt16LittleEndian(Span[NtfsLayout.AttrNameOffset..]);
                return MemoryMarshal.Cast<byte, char>(Span.Slice(offset, length * sizeof(char)));
            }
        }

        /// <summary>Resident value (empty for nonresident attributes).</summary>
        public ReadOnlySpan<byte> Value
        {
            get
            {
                if (!IsResident)
                {
                    return default;
                }

                int length = BinaryPrimitives.ReadInt32LittleEndian(Span[NtfsLayout.AttrResidentValueLength..]);
                int offset = BinaryPrimitives.ReadUInt16LittleEndian(Span[NtfsLayout.AttrResidentValueOffset..]);
                return offset + length <= Span.Length ? Span.Slice(offset, length) : default;
            }
        }

        public long LowestVcn => IsResident ? 0 : BinaryPrimitives.ReadInt64LittleEndian(Span[NtfsLayout.AttrLowestVcn..]);

        /// <summary>Logical size. Only valid when LowestVcn is 0.</summary>
        public long FileSize => IsResident
            ? BinaryPrimitives.ReadUInt32LittleEndian(Span[NtfsLayout.AttrResidentValueLength..])
            : BinaryPrimitives.ReadInt64LittleEndian(Span[NtfsLayout.AttrFileSize..]);

        /// <summary>Allocated length (multiple of the cluster size). Only valid when LowestVcn is 0.</summary>
        public long AllocatedLength => IsResident ? 0 : BinaryPrimitives.ReadInt64LittleEndian(Span[NtfsLayout.AttrAllocatedLength..]);

        /// <summary>
        /// Clusters actually allocated. TotalAllocated is used when the nonresident header is long enough to contain it
        /// (mapping pairs and name start at or after offset 72) and the attribute is compressed or sparse; otherwise AllocatedLength.
        /// </summary>
        public long ActualAllocated
        {
            get
            {
                if (IsResident)
                {
                    return 0;
                }

                bool compressedOrSparse = (Flags & (NtfsLayout.ATTRIBUTE_FLAG_COMPRESSION_MASK | NtfsLayout.ATTRIBUTE_FLAG_SPARSE)) != 0;
                int headerEnd = BinaryPrimitives.ReadUInt16LittleEndian(Span[NtfsLayout.AttrMappingPairsOffset..]);
                if (Span[NtfsLayout.AttrNameLength] != 0)
                {
                    headerEnd = Math.Min(headerEnd, BinaryPrimitives.ReadUInt16LittleEndian(Span[NtfsLayout.AttrNameOffset..]));
                }

                return compressedOrSparse && headerEnd >= NtfsLayout.AttrNonresidentHeaderWithTotalAllocated
                    ? BinaryPrimitives.ReadInt64LittleEndian(Span[NtfsLayout.AttrTotalAllocated..])
                    : AllocatedLength;
            }
        }

        /// <summary>
        /// Decodes the mapping pairs array (ATTRIBUTE_RECORD_HEADER remarks): count byte = v + 16 * l,
        /// followed by v bytes of VCN delta and l bytes of LCN delta, both signed little-endian.
        /// </summary>
        public bool DecodeMappingPairs(List<Extent> extents)
        {
            if (IsResident)
            {
                return false;
            }

            int offset = BinaryPrimitives.ReadUInt16LittleEndian(Span[NtfsLayout.AttrMappingPairsOffset..]);
            long nextVcn = LowestVcn;
            long currentLcn = 0;
            while (offset < Span.Length)
            {
                long currentVcn = nextVcn;
                byte count = Span[offset++];
                if (count == 0)
                {
                    return true;
                }

                int v = count & 0x0F;
                int l = count >> 4;
                if (v == 0 || v > 8 || l > 8 || offset + v + l > Span.Length)
                {
                    return false;
                }

                nextVcn += ReadSigned(Span.Slice(offset, v));
                offset += v;

                bool allocated = l != 0;
                if (l != 0)
                {
                    currentLcn += ReadSigned(Span.Slice(offset, l));
                    offset += l;
                    allocated = currentLcn != 0;
                }

                extents.Add(new Extent(currentVcn, allocated ? currentLcn : -1, nextVcn - currentVcn));
            }

            return false;
        }

        private static long ReadSigned(ReadOnlySpan<byte> bytes)
        {
            long value = 0;
            for (int i = bytes.Length - 1; i >= 0; i--)
            {
                value = (value << 8) | bytes[i];
            }

            int shift = 64 - (bytes.Length * 8);
            return (value << shift) >> shift;
        }
    }
}
