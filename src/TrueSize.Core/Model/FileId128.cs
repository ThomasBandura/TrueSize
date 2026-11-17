using System.Buffers.Binary;

namespace TrueSize.Core.Model;

/// <summary>128-bit file identifier (FILE_ID_128). Unique per volume on NTFS and ReFS.</summary>
public readonly record struct FileId128(ulong Low, ulong High) : IComparable<FileId128>
{
    public static FileId128 FromBytes(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));

    public bool IsZero => Low == 0 && High == 0;

    public int CompareTo(FileId128 other)
    {
        int c = High.CompareTo(other.High);
        return c != 0 ? c : Low.CompareTo(other.Low);
    }

    public override string ToString() => $"{High:x16}{Low:x16}";
}
