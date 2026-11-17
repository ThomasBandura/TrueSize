using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrueSize.Core.Interop;

internal readonly record struct StreamTotals(long UnnamedSize, long UnnamedAllocated, long AlternateSize, long AlternateAllocated, int AlternateCount);

internal static unsafe class FileQueries
{
    private static volatile bool s_byNameUnavailable;

    /// <summary>Converts a full Win32 path into an extended-length path (\\?\ or \\?\UNC\).</summary>
    public static string ToExtendedPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return string.Concat(@"\\?\UNC\", fullPath.AsSpan(2));
        }

        return @"\\?\" + fullPath;
    }

    /// <summary>
    /// GetFileInformationByName (FileStatBasicByNameInfo). Returns false with the Win32 error on failure,
    /// or with error 0 when the API does not exist on this Windows version.
    /// </summary>
    public static bool TryStatByName(char* path, out FILE_STAT_BASIC_INFORMATION info, out int error)
    {
        info = default;
        if (s_byNameUnavailable)
        {
            error = 0;
            return false;
        }

        try
        {
            fixed (FILE_STAT_BASIC_INFORMATION* p = &info)
            {
                if (Native.GetFileInformationByName(path, FileInfoByNameClass.FileStatBasicByNameInfo, p, (uint)sizeof(FILE_STAT_BASIC_INFORMATION)))
                {
                    error = 0;
                    return true;
                }
            }

            error = Marshal.GetLastPInvokeError();
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            s_byNameUnavailable = true;
            error = 0;
            return false;
        }
    }

    /// <summary>Opens a file or directory for attribute queries only. Never follows reparse points and never reads data.</summary>
    public static SafeFileHandle OpenForAttributes(char* path) =>
        Native.CreateFile(
            path,
            Native.FILE_READ_ATTRIBUTES | Native.SYNCHRONIZE,
            Native.FILE_SHARE_ALL,
            0,
            Native.OPEN_EXISTING,
            Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT,
            0);

    public static SafeFileHandle OpenDirectoryForListing(char* path, bool followReparsePoint) =>
        Native.CreateFile(
            path,
            Native.FILE_LIST_DIRECTORY | Native.SYNCHRONIZE,
            Native.FILE_SHARE_ALL,
            0,
            Native.OPEN_EXISTING,
            Native.FILE_FLAG_BACKUP_SEMANTICS | (followReparsePoint ? 0 : Native.FILE_FLAG_OPEN_REPARSE_POINT),
            0);

    public static bool TryGetStandardInfo(SafeFileHandle handle, out FILE_STANDARD_INFO info, out int error)
    {
        fixed (FILE_STANDARD_INFO* p = &info)
        {
            if (Native.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileStandardInfo, p, (uint)sizeof(FILE_STANDARD_INFO)))
            {
                error = 0;
                return true;
            }
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>Compressed (actually allocated) size of the unnamed data stream. Valid for compressed, sparse and WOF files.</summary>
    public static bool TryGetCompressedSize(SafeFileHandle handle, out long compressedSize, out int error)
    {
        FILE_COMPRESSION_INFO info;
        if (Native.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileCompressionInfo, &info, (uint)sizeof(FILE_COMPRESSION_INFO)))
        {
            compressedSize = info.CompressedFileSize;
            error = 0;
            return true;
        }

        compressedSize = 0;
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <summary>
    /// Allocation of a compressed/WOF/dedup file. CompressedFileSize is the allocated size but capped at the logical size
    /// (measured: a 12-byte compressed file reports 12, a 960-byte one 960, while 16 resp. 4096 bytes are allocated).
    /// When the cap is hit, FileStandardInfo.AllocationSize is used instead.
    /// </summary>
    public static bool TryGetCompressedAllocation(SafeFileHandle handle, long endOfFile, out long allocated, out int error)
    {
        if (!TryGetCompressedSize(handle, out allocated, out error))
        {
            return false;
        }

        if (allocated >= endOfFile && TryGetStandardInfo(handle, out var std, out error))
        {
            allocated = std.AllocationSize;
        }

        return true;
    }

    /// <summary>Sums all $DATA streams. <paramref name="buffer"/> grows as needed and can be reused.</summary>
    public static bool TryGetStreams(SafeFileHandle handle, ref byte[] buffer, out StreamTotals totals, out int error)
    {
        totals = default;
        while (true)
        {
            fixed (byte* p = buffer)
            {
                if (Native.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileStreamInfo, p, (uint)buffer.Length))
                {
                    totals = SumStreams(p);
                    error = 0;
                    return true;
                }
            }

            error = Marshal.GetLastPInvokeError();
            if (error == Native.ERROR_HANDLE_EOF)
            {
                // No streams at all (e.g. a directory without alternate streams).
                error = 0;
                return true;
            }

            if (error != Native.ERROR_MORE_DATA || buffer.Length >= 16 * 1024 * 1024)
            {
                return false;
            }

            buffer = new byte[buffer.Length * 2];
        }
    }

    private static StreamTotals SumStreams(byte* p)
    {
        long unnamedSize = 0, unnamedAllocated = 0, altSize = 0, altAllocated = 0;
        int altCount = 0;
        int offset = 0;
        while (true)
        {
            var s = (FILE_STREAM_INFO*)(p + offset);
            var name = new ReadOnlySpan<char>(&s->StreamName, (int)(s->StreamNameLength / sizeof(char)));
            if (name.SequenceEqual("::$DATA"))
            {
                unnamedSize = s->StreamSize;
                unnamedAllocated = s->StreamAllocationSize;
            }
            else
            {
                altSize += s->StreamSize;
                altAllocated += s->StreamAllocationSize;
                altCount++;
            }

            if (s->NextEntryOffset == 0)
            {
                break;
            }

            offset += (int)s->NextEntryOffset;
        }

        return new StreamTotals(unnamedSize, unnamedAllocated, altSize, altAllocated, altCount);
    }
}
