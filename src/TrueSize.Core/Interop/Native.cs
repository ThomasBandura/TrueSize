using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrueSize.Core.Interop;

// All definitions verified against Windows SDK 10.0.26100.0 headers:
// winnt.h, WinBase.h, minwinbase.h, fileapi.h, ioapiset.h, winioctl.h, winerror.h.
internal static unsafe partial class Native
{
    // winnt.h access rights
    public const uint FILE_READ_DATA = 0x0001;
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_WRITE_DATA = 0x0002;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint SYNCHRONIZE = 0x00100000;

    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;
    public const uint FILE_SHARE_ALL = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    // fileapi.h
    public const uint OPEN_EXISTING = 3;

    // WinBase.h
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    // winnt.h file attributes
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    public const uint FILE_ATTRIBUTE_COMPRESSED = 0x00000800;
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x00001000;
    public const uint FILE_ATTRIBUTE_ENCRYPTED = 0x00004000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

    // winnt.h reparse tags
    public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;
    public const uint IO_REPARSE_TAG_DEDUP = 0x80000013;
    public const uint IO_REPARSE_TAG_WOF = 0x80000017;
    public const uint IO_REPARSE_TAG_CLOUD = 0x9000001A;
    public const uint IO_REPARSE_TAG_CLOUD_MASK = 0x0000F000;

    /// <summary>IsReparseTagNameSurrogate: bit 0x20000000.</summary>
    public static bool IsReparseTagNameSurrogate(uint tag) => (tag & 0x20000000) != 0;

    public static bool IsCloudTag(uint tag) => (tag & ~IO_REPARSE_TAG_CLOUD_MASK) == IO_REPARSE_TAG_CLOUD;

    // winerror.h
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_MORE_DATA = 234;

    // winioctl.h: CTL_CODE(DeviceType, Function, Method, Access) = (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
    private const uint FILE_DEVICE_FILE_SYSTEM = 0x00000009;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;
    private const uint FILE_SPECIAL_ACCESS = FILE_ANY_ACCESS;

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    public static readonly uint FSCTL_SET_COMPRESSION = CtlCode(FILE_DEVICE_FILE_SYSTEM, 16, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA);
    public static readonly uint FSCTL_SET_SPARSE = CtlCode(FILE_DEVICE_FILE_SYSTEM, 49, METHOD_BUFFERED, FILE_SPECIAL_ACCESS);
    public static readonly uint FSCTL_GET_NTFS_VOLUME_DATA = CtlCode(FILE_DEVICE_FILE_SYSTEM, 25, METHOD_BUFFERED, FILE_ANY_ACCESS);
    private const uint METHOD_NEITHER = 3;
    public static readonly uint FSCTL_ENUM_USN_DATA = CtlCode(FILE_DEVICE_FILE_SYSTEM, 44, METHOD_NEITHER, FILE_ANY_ACCESS);

    // winerror.h
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;

    /// <summary>WinBase.h OpenFileById.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint, FILE_ID_DESCRIPTOR* fileId, uint desiredAccess, uint shareMode, nint securityAttributes, uint flagsAndAttributes);

    // winnt.h
    public const uint GENERIC_READ = 0x80000000;

    /// <summary>fileapi.h ReadFile. With a synchronous handle and an OVERLAPPED offset it reads at that offset and blocks.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle file, void* buffer, uint numberOfBytesToRead, out uint numberOfBytesRead, OVERLAPPED* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, char* volumeName, uint bufferLength);

    // winnt.h
    public const ushort COMPRESSION_FORMAT_DEFAULT = 0x0001;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true)]
    public static partial SafeFileHandle CreateFile(
        char* fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass infoClass, void* buffer, uint bufferSize);

    /// <summary>Windows 11 24H2+. Queries metadata by path without returning a handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByName(char* fileName, FileInfoByNameClass infoClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    /// <summary>WinBase.h GetNamedPipeClientProcessId.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>WinBase.h GetNamedPipeServerProcessId.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    // winerror.h
    public const int ERROR_CANCELLED = 1223;

    /// <summary>fileapi.h GetDiskFreeSpaceExW.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailableToCaller, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(
        string rootPathName,
        char* volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        char* fileSystemNameBuffer,
        uint fileSystemNameSize);
}

/// <summary>minwinbase.h FILE_INFO_BY_HANDLE_CLASS (implicit enumerator values).</summary>
internal enum FileInfoByHandleClass
{
    FileStandardInfo = 1,
    FileStreamInfo = 7,
    FileCompressionInfo = 8,
    FileIdInfo = 18,
    FileIdExtdDirectoryInfo = 19,
    FileIdExtdDirectoryRestartInfo = 20,
}

/// <summary>minwinbase.h FILE_INFO_BY_NAME_CLASS.</summary>
internal enum FileInfoByNameClass
{
    FileStatByNameInfo = 0,
    FileStatBasicByNameInfo = 3,
}

/// <summary>WinBase.h FILE_ID_EXTD_DIR_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_EXTD_DIR_INFO
{
    public uint NextEntryOffset;
    public uint FileIndex;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public long EndOfFile;
    public long AllocationSize;
    public uint FileAttributes;
    public uint FileNameLength; // bytes
    public uint EaSize;
    public uint ReparsePointTag;
    public FILE_ID_128 FileId;
    public char FileName; // first character of a variable-length name
}

/// <summary>winnt.h FILE_ID_128.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_128
{
    public ulong Low;
    public ulong High;
}

/// <summary>winioctl.h MFT_ENUM_DATA_V1 (input of FSCTL_ENUM_USN_DATA).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFT_ENUM_DATA_V1
{
    public ulong StartFileReferenceNumber;
    public long LowUsn;
    public long HighUsn;
    public ushort MinMajorVersion;
    public ushort MaxMajorVersion;
}

/// <summary>WinBase.h FILE_ID_DESCRIPTOR with the FILE_ID_128 union member (Type = ExtendedFileIdType = 2).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_DESCRIPTOR
{
    public const int ExtendedFileIdType = 2; // FILE_ID_TYPE: FileIdType, ObjectIdType, ExtendedFileIdType

    public uint dwSize;
    public int Type;

    // Union { LARGE_INTEGER FileId; GUID ObjectId; FILE_ID_128 ExtendedFileId; } aligned to 8 bytes.
    public FILE_ID_128 ExtendedFileId;
}

/// <summary>minwinbase.h OVERLAPPED (the Offset/OffsetHigh member of the union).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OVERLAPPED
{
    public nuint Internal;
    public nuint InternalHigh;
    public uint Offset;
    public uint OffsetHigh;
    public nint hEvent;
}

/// <summary>WinBase.h FILE_ID_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_INFO
{
    public ulong VolumeSerialNumber;
    public FILE_ID_128 FileId;
}

/// <summary>winioctl.h NTFS_VOLUME_DATA_BUFFER (output of FSCTL_GET_NTFS_VOLUME_DATA).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NTFS_VOLUME_DATA_BUFFER
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}

/// <summary>WinBase.h FILE_STANDARD_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_STANDARD_INFO
{
    public long AllocationSize;
    public long EndOfFile;
    public uint NumberOfLinks;
    public byte DeletePending;
    public byte Directory;
}

/// <summary>WinBase.h FILE_STREAM_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_STREAM_INFO
{
    public uint NextEntryOffset;
    public uint StreamNameLength; // bytes
    public long StreamSize;
    public long StreamAllocationSize;
    public char StreamName;
}

/// <summary>WinBase.h FILE_COMPRESSION_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_COMPRESSION_INFO
{
    public long CompressedFileSize;
    public ushort CompressionFormat;
    public byte CompressionUnitShift;
    public byte ChunkShift;
    public byte ClusterShift;
    public byte Reserved0;
    public byte Reserved1;
    public byte Reserved2;
}

/// <summary>winnt.h FILE_STAT_BASIC_INFORMATION (Windows 11 24H2+).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FILE_STAT_BASIC_INFORMATION
{
    public long FileId;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public long AllocationSize;
    public long EndOfFile;
    public uint FileAttributes;
    public uint ReparseTag;
    public uint NumberOfLinks;
    public uint DeviceType;
    public uint DeviceCharacteristics;
    public uint Reserved;
    public long VolumeSerialNumber;
    public FILE_ID_128 FileId128;
}
