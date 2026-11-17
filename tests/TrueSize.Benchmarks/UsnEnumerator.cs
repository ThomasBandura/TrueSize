using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TrueSize.Core.Interop;
using TrueSize.Core.Model;

namespace TrueSize.Benchmarks;

/// <summary>One entry returned by FSCTL_ENUM_USN_DATA (USN_RECORD_V2 or USN_RECORD_V3).</summary>
internal readonly record struct UsnEntry(FileId128 Id, FileId128 Parent, uint Attributes, string Name, int MajorVersion);

/// <summary>
/// Enumerates all files of a volume via FSCTL_ENUM_USN_DATA with MFT_ENUM_DATA_V1 (record versions 2 and 3).
/// Documentation conflict: FSCTL_ENUM_USN_DATA says "The volume must be NTFS", MFT_ENUM_DATA_V1 is described as
/// "for ReFS volumes". Phase 4 result (docs/refs.md): ERROR_INVALID_FUNCTION on ReFS 3.14 even with an active journal.
/// Kept in the benchmark tool only, to re-check future Windows versions (--usn-probe).
/// </summary>
internal static unsafe class UsnEnumerator
{
    // USN_RECORD_V2: RecordLength 0, MajorVersion 4, MinorVersion 6, FileReferenceNumber 8 (8), ParentFileReferenceNumber 16 (8),
    //   Usn 24, TimeStamp 32, Reason 40, SourceInfo 44, SecurityId 48, FileAttributes 52, FileNameLength 56, FileNameOffset 58.
    // USN_RECORD_V3: RecordLength 0, MajorVersion 4, MinorVersion 6, FileReferenceNumber 8 (16), ParentFileReferenceNumber 24 (16),
    //   Usn 40, TimeStamp 48, Reason 56, SourceInfo 60, SecurityId 64, FileAttributes 68, FileNameLength 72, FileNameOffset 74.

    /// <summary>Opens the volume device that contains <paramref name="volumeRoot"/> (administrator).</summary>
    public static SafeFileHandle OpenVolume(string volumeRoot)
    {
        char* name = stackalloc char[64];
        if (!Native.GetVolumeNameForVolumeMountPoint(volumeRoot, name, 64))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        string device = new string(name).TrimEnd('\\');
        fixed (char* p = device)
        {
            var handle = Native.CreateFile(p, Native.GENERIC_READ, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING, 0, 0);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new Win32Exception(error, $"Opening volume '{device}' failed.");
            }

            return handle;
        }
    }

    /// <summary>Calls <paramref name="onEntry"/> for every record. Returns 0 on success or the Win32 error that stopped it.</summary>
    public static int Enumerate(SafeFileHandle volume, Action<UsnEntry> onEntry, CancellationToken cancellationToken = default)
    {
        const int bufferSize = 1024 * 1024;
        byte* buffer = (byte*)NativeMemory.AlignedAlloc(bufferSize, 8);
        try
        {
            var input = new MFT_ENUM_DATA_V1 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue, MinMajorVersion = 2, MaxMajorVersion = 3 };
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Native.DeviceIoControl(volume, Native.FSCTL_ENUM_USN_DATA, &input, (uint)sizeof(MFT_ENUM_DATA_V1), buffer, bufferSize, out uint returned, 0))
                {
                    int error = Marshal.GetLastPInvokeError();
                    return error == Native.ERROR_HANDLE_EOF ? 0 : error;
                }

                // "Each call ... retrieves the starting point for the subsequent call as the first entry in the output buffer."
                var data = new ReadOnlySpan<byte>(buffer, (int)returned);
                if (data.Length < 8)
                {
                    return 0;
                }

                input.StartFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(data);
                int offset = 8;
                while (offset + 8 <= data.Length)
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
                    if (length <= 0 || offset + length > data.Length)
                    {
                        break;
                    }

                    onEntry(Parse(data.Slice(offset, length)));
                    offset += length;
                }

                if (offset == 8)
                {
                    return 0; // no records: end of enumeration
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private static UsnEntry Parse(ReadOnlySpan<byte> r)
    {
        int major = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
        FileId128 id, parent;
        uint attributes;
        int nameLength, nameOffset;
        if (major == 3)
        {
            id = FileId128.FromBytes(r[8..]);
            parent = FileId128.FromBytes(r[24..]);
            attributes = BinaryPrimitives.ReadUInt32LittleEndian(r[68..]);
            nameLength = BinaryPrimitives.ReadUInt16LittleEndian(r[72..]);
            nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(r[74..]);
        }
        else
        {
            id = new FileId128(BinaryPrimitives.ReadUInt64LittleEndian(r[8..]), 0);
            parent = new FileId128(BinaryPrimitives.ReadUInt64LittleEndian(r[16..]), 0);
            attributes = BinaryPrimitives.ReadUInt32LittleEndian(r[52..]);
            nameLength = BinaryPrimitives.ReadUInt16LittleEndian(r[56..]);
            nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(r[58..]);
        }

        string name = MemoryMarshal.Cast<byte, char>(r.Slice(nameOffset, nameLength)).ToString();
        return new UsnEntry(id, parent, attributes, name, major);
    }

    /// <summary>Opens a file by its 128-bit id (FILE_ID_DESCRIPTOR.ExtendedFileIdType) for attribute queries only.</summary>
    public static SafeFileHandle OpenById(SafeFileHandle volumeHint, FileId128 id)
    {
        var descriptor = new FILE_ID_DESCRIPTOR
        {
            dwSize = (uint)sizeof(FILE_ID_DESCRIPTOR),
            Type = FILE_ID_DESCRIPTOR.ExtendedFileIdType,
            ExtendedFileId = new FILE_ID_128 { Low = id.Low, High = id.High },
        };
        return Native.OpenFileById(
            volumeHint,
            &descriptor,
            Native.FILE_READ_ATTRIBUTES | Native.SYNCHRONIZE,
            Native.FILE_SHARE_ALL,
            0,
            Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT);
    }
}
