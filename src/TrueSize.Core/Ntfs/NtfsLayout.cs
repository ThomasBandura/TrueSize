using System.Buffers.Binary;

namespace TrueSize.Core.Ntfs;

/// <summary>
/// On-disk NTFS 3.x structures as documented by Microsoft
/// (https://learn.microsoft.com/windows/win32/devnotes/master-file-table and linked pages).
/// Offsets are derived from the documented C declarations with natural alignment.
/// </summary>
internal static class NtfsLayout
{
    // FILE_RECORD_SEGMENT_HEADER
    //   MULTI_SECTOR_HEADER MultiSectorHeader;  // 0: UCHAR Signature[4]; USHORT UpdateSequenceArrayOffset (4); USHORT UpdateSequenceArraySize (6)
    //   ULONGLONG Reserved1;                    // 8
    //   USHORT SequenceNumber;                  // 16
    //   USHORT Reserved2;                       // 18
    //   USHORT FirstAttributeOffset;            // 20
    //   USHORT Flags;                           // 22
    //   ULONG Reserved3[2];                     // 24
    //   FILE_REFERENCE BaseFileRecordSegment;   // 32
    public const int RecordUsaOffset = 4;
    public const int RecordUsaSize = 6;
    public const int RecordSequenceNumber = 16;
    public const int RecordFirstAttributeOffset = 20;
    public const int RecordFlags = 22;
    public const int RecordBaseFileRecordSegment = 32;

    public const ushort FILE_RECORD_SEGMENT_IN_USE = 0x0001;

    // Documented name "FILE_FILE_NAME_INDEX_PRESENT": the record has a file name index, i.e. it is a directory.
    public const ushort FILE_FILE_NAME_INDEX_PRESENT = 0x0002;

    /// <summary>"The MULTI_SECTOR_HEADER structure always contains the signature "FILE"".</summary>
    public const uint FileSignature = 'F' | ('I' << 8) | ('L' << 16) | ('E' << 24);

    /// <summary>MULTI_SECTOR_HEADER remarks: sequence number stride.</summary>
    public const int SequenceStride = 512;

    // MFT special records (master-file-table page).
    public const long MftRecord = 0;
    public const long RootDirectoryRecord = 5;
    public const long FirstUserRecord = 16;

    // ATTRIBUTE_RECORD_HEADER
    //   ATTRIBUTE_TYPE_CODE TypeCode; // 0
    //   ULONG RecordLength;           // 4
    //   UCHAR FormCode;               // 8
    //   UCHAR NameLength;             // 9
    //   USHORT NameOffset;            // 10
    //   USHORT Flags;                 // 12
    //   USHORT Instance;              // 14
    //   union Form                    // 16
    //     Resident:    ULONG ValueLength (16); USHORT ValueOffset (20); UCHAR Reserved[2]
    //     Nonresident: VCN LowestVcn (16); VCN HighestVcn (24); USHORT MappingPairsOffset (32); UCHAR Reserved[6];
    //                  LONGLONG AllocatedLength (40); FileSize (48); ValidDataLength (56); TotalAllocated (64)
    public const int AttrTypeCode = 0;
    public const int AttrRecordLength = 4;
    public const int AttrFormCode = 8;
    public const int AttrNameLength = 9;
    public const int AttrNameOffset = 10;
    public const int AttrFlags = 12;
    public const int AttrResidentValueLength = 16;
    public const int AttrResidentValueOffset = 20;
    public const int AttrLowestVcn = 16;
    public const int AttrMappingPairsOffset = 32;
    public const int AttrAllocatedLength = 40;
    public const int AttrFileSize = 48;
    public const int AttrTotalAllocated = 64;
    public const int AttrNonresidentHeaderWithTotalAllocated = 72;

    public const byte RESIDENT_FORM = 0x00;
    public const byte NONRESIDENT_FORM = 0x01;

    public const ushort ATTRIBUTE_FLAG_COMPRESSION_MASK = 0x00FF;
    public const ushort ATTRIBUTE_FLAG_ENCRYPTED = 0x4000;
    public const ushort ATTRIBUTE_FLAG_SPARSE = 0x8000;

    // Attribute type codes (ATTRIBUTE_RECORD_HEADER page); $END from the master-file-table page.
    public const uint STANDARD_INFORMATION = 0x10;
    public const uint ATTRIBUTE_LIST = 0x20;
    public const uint FILE_NAME = 0x30;
    public const uint DATA = 0x80;
    public const uint INDEX_ROOT = 0x90;
    public const uint INDEX_ALLOCATION = 0xA0;
    public const uint REPARSE_POINT = 0xC0;
    public const uint END = 0xFFFFFFFF;

    // ATTRIBUTE_LIST_ENTRY
    //   ATTRIBUTE_TYPE_CODE AttributeTypeCode; // 0
    //   USHORT RecordLength;                   // 4
    //   UCHAR AttributeNameLength;             // 6
    //   UCHAR AttributeNameOffset;             // 7 (reserved)
    //   VCN LowestVcn;                         // 8
    //   MFT_SEGMENT_REFERENCE SegmentReference;// 16
    public const int ListTypeCode = 0;
    public const int ListRecordLength = 4;
    public const int ListLowestVcn = 8;
    public const int ListSegmentReference = 16;

    // FILE_NAME
    //   FILE_REFERENCE ParentDirectory; // 0
    //   UCHAR Reserved[0x38];           // 8
    //   UCHAR FileNameLength;           // 0x40 (characters)
    //   UCHAR Flags;                    // 0x41
    //   WCHAR FileName[1];              // 0x42
    public const int FileNameParent = 0;
    public const int FileNameLength = 0x40;
    public const int FileNameFlags = 0x41;
    public const int FileNameChars = 0x42;

    // UNDOCUMENTED (inside the documented Reserved[0x38] of FILE_NAME; approved for use, verified by
    // NtfsMftScannerTests.Live_UserProfile_ReparseTagsMatchEnumeration against FileIdExtdDirectoryInfo.ReparsePointTag):
    //   0x38 ULONG FileAttributes (copy of the file attributes)
    //   0x3C ULONG ReparsePointTag when FILE_ATTRIBUTE_REPARSE_POINT is set (otherwise the EA size)
    // Used only to avoid one cluster read per nonresident $REPARSE_POINT; the cluster read remains the fallback.
    public const int FileNameAttributesUndocumented = 0x38;
    public const int FileNameReparseTagUndocumented = 0x3C;

    public const byte FILE_NAME_NTFS = 0x01;
    public const byte FILE_NAME_DOS = 0x02;

    /// <summary>
    /// MFT_SEGMENT_REFERENCE { ULONG SegmentNumberLowPart; USHORT SegmentNumberHighPart; USHORT SequenceNumber; }.
    /// The segment number is combined as LowPart | HighPart &lt;&lt; 32 (48 bits).
    /// </summary>
    public static (long Segment, ushort Sequence) ReadSegmentReference(ReadOnlySpan<byte> span)
    {
        uint low = BinaryPrimitives.ReadUInt32LittleEndian(span);
        ushort high = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        return (low | ((long)high << 32), sequence);
    }
}
