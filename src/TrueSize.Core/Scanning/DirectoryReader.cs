using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TrueSize.Core.Interop;
using TrueSize.Core.Model;

namespace TrueSize.Core.Scanning;

/// <summary>
/// Reads a full directory listing via GetFileInformationByHandleEx(FileIdExtdDirectoryInfo).
/// One instance per worker thread; buffers are reused across directories.
/// </summary>
internal sealed unsafe class DirectoryReader : IDisposable
{
    private readonly byte* _buffer;
    private readonly uint _bufferSize;

    public DirectoryReader(int bufferSize)
    {
        _bufferSize = (uint)Math.Max(bufferSize, 4096);
        _buffer = (byte*)NativeMemory.AlignedAlloc(_bufferSize, 8);
    }

    public EntryData[] Entries = new EntryData[256];
    public int Count;
    public char[] Names = new char[8192];
    public int NamesLength;

    public ReadOnlySpan<EntryData> EntrySpan => Entries.AsSpan(0, Count);

    public ReadOnlySpan<char> NameOf(in EntryData e) => Names.AsSpan(e.NameStart, e.NameLength);

    /// <summary>Reads all entries except "." and "..". Returns 0 or the Win32 error that stopped the listing.</summary>
    public int Read(SafeFileHandle directory)
    {
        Count = 0;
        NamesLength = 0;
        while (true)
        {
            if (!Native.GetFileInformationByHandleEx(directory, FileInfoByHandleClass.FileIdExtdDirectoryInfo, _buffer, _bufferSize))
            {
                int error = Marshal.GetLastPInvokeError();
                return error == Native.ERROR_NO_MORE_FILES ? 0 : error;
            }

            uint offset = 0;
            while (true)
            {
                var info = (FILE_ID_EXTD_DIR_INFO*)(_buffer + offset);
                var name = new ReadOnlySpan<char>(&info->FileName, (int)(info->FileNameLength / sizeof(char)));
                if (!(name is "." or ".."))
                {
                    Add(info, name);
                }

                if (info->NextEntryOffset == 0)
                {
                    break;
                }

                offset += info->NextEntryOffset;
            }
        }
    }

    private void Add(FILE_ID_EXTD_DIR_INFO* info, ReadOnlySpan<char> name)
    {
        if (Count == Entries.Length)
        {
            Array.Resize(ref Entries, Entries.Length * 2);
        }

        if (NamesLength + name.Length > Names.Length)
        {
            Array.Resize(ref Names, Math.Max(Names.Length * 2, NamesLength + name.Length));
        }

        name.CopyTo(Names.AsSpan(NamesLength));
        uint tag = (info->FileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? info->ReparsePointTag : 0;
        Entries[Count++] = new EntryData
        {
            Flags = EntryClassifier.Classify(info->FileAttributes, tag),
            Size = info->EndOfFile,
            Allocated = info->AllocationSize,
            FileId = new FileId128(info->FileId.Low, info->FileId.High),
            LinkCount = 0,
            ReparseTag = tag,
            NameStart = NamesLength,
            NameLength = name.Length,
        };
        NamesLength += name.Length;
    }

    public void Dispose() => NativeMemory.AlignedFree(_buffer);
}

/// <summary>Builds NUL-terminated extended-length paths "dir\name" in a reusable buffer.</summary>
internal sealed class PathBuffer
{
    private char[] _buffer = new char[1024];
    private int _dirLength;

    public void SetDirectory(string extendedDirectoryPath)
    {
        Ensure(extendedDirectoryPath.Length + 2);
        extendedDirectoryPath.CopyTo(_buffer);
        _dirLength = extendedDirectoryPath.Length;
        if (_buffer[_dirLength - 1] != '\\')
        {
            _buffer[_dirLength++] = '\\';
        }

        _buffer[_dirLength] = '\0';
    }

    /// <summary>Directory path itself, NUL-terminated (with trailing backslash).</summary>
    public ref char Directory => ref _buffer[0];

    public ref char Child(ReadOnlySpan<char> name)
    {
        Ensure(_dirLength + name.Length + 1);
        name.CopyTo(_buffer.AsSpan(_dirLength));
        _buffer[_dirLength + name.Length] = '\0';
        return ref _buffer[0];
    }

    private void Ensure(int length)
    {
        if (length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(length, _buffer.Length * 2));
        }
    }
}
