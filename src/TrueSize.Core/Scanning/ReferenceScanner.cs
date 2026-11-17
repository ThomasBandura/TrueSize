using System.Diagnostics;
using System.Runtime.InteropServices;
using TrueSize.Core.Interop;
using TrueSize.Core.Model;

namespace TrueSize.Core.Scanning;

/// <summary>
/// Slow, single-threaded ground truth for tests. Every entry (file and directory) is opened for attributes
/// and queried with FileStandardInfo, FileStreamInfo and, where relevant, FileCompressionInfo. Directory entry
/// sizes are never used except for cloud placeholders, which are not opened.
/// </summary>
public sealed class ReferenceScanner : IVolumeScanner
{
    public string Name => "Reference";

    public unsafe ScanResult Scan(string path, ScanOptions options, ScanProgress? progress = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        progress ??= new ScanProgress();
        string root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var volume = VolumeInfo.FromPath(root);

        // Elevated: backup semantics + SeBackupPrivilege read every directory regardless of ACLs (same view as the MFT).
        if (Elevation.IsProcessElevated())
        {
            Privileges.TryEnableBackupPrivilege();
        }
        var builder = new ScanTreeBuilder();
        var errors = new List<ScanError>();
        using var reader = new DirectoryReader(options.DirectoryBufferSize);
        var paths = new PathBuffer();
        var streamBuffer = new byte[4096];
        var indices = new int[256];

        EntryData rootData = default;
        paths.SetDirectory(FileQueries.ToExtendedPath(root));
        fixed (char* r = &paths.Directory)
        {
            using var rootHandle = FileQueries.OpenForAttributes(r);
            if (!rootHandle.IsInvalid && FileQueries.TryGetStandardInfo(rootHandle, out var rootInfo, out _))
            {
                rootData.Allocated = rootInfo.AllocationSize;
            }
        }

        var stack = new Stack<(int Node, string Path, bool IsRoot)>();
        stack.Push((builder.AddRoot(root, rootData), root, true));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, dirPath, isRoot) = stack.Pop();
            paths.SetDirectory(FileQueries.ToExtendedPath(dirPath));

            int listError;
            fixed (char* dir = &paths.Directory)
            {
                using var handle = FileQueries.OpenDirectoryForListing(dir, followReparsePoint: isRoot);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError();
                    errors.Add(new ScanError(dirPath, error, "OpenDirectory"));
                    builder.SetFlags(node, error == Native.ERROR_ACCESS_DENIED ? NodeFlags.AccessDenied : NodeFlags.Error);
                    continue;
                }

                listError = reader.Read(handle);
            }

            if (listError != 0)
            {
                errors.Add(new ScanError(dirPath, listError, "ListDirectory"));
                builder.SetFlags(node, NodeFlags.Error);
            }

            for (int i = 0; i < reader.Count; i++)
            {
                ref EntryData e = ref reader.Entries[i];
                if (!EntryClassifier.MayOpen(e.Flags))
                {
                    e.LinkCount = (e.Flags & NodeFlags.Directory) != 0 ? 0 : 1;
                    continue;
                }

                fixed (char* p = &paths.Child(reader.NameOf(e)))
                {
                    using var handle = FileQueries.OpenForAttributes(p);
                    if (handle.IsInvalid)
                    {
                        errors.Add(new ScanError(Path.Join(dirPath, reader.NameOf(e)), Marshal.GetLastPInvokeError(), "OpenFile"));
                        e.Flags |= NodeFlags.Error;
                        continue;
                    }

                    QueryExact(handle, ref e, ref streamBuffer, options, out int queryError);
                    if (queryError != 0)
                    {
                        errors.Add(new ScanError(Path.Join(dirPath, reader.NameOf(e)), queryError, "QueryFile"));
                        e.Flags |= NodeFlags.Error;
                    }
                }
            }

            if (indices.Length < reader.Count)
            {
                indices = new int[reader.Count * 2];
            }

            builder.AddChildren(node, reader.EntrySpan, reader.Names.AsSpan(0, reader.NamesLength), indices);

            for (int i = 0; i < reader.Count; i++)
            {
                ref EntryData e = ref reader.Entries[i];
                if ((e.Flags & NodeFlags.Directory) != 0 && !EntryClassifier.ShouldSkipDirectory(e.Flags, options))
                {
                    stack.Push((indices[i], Path.Join(dirPath, reader.NameOf(e)), false));
                }
            }
        }

        var tree = builder.Build(new ScanTreeInfo(root, volume.Root, volume.SerialNumber, volume.FileSystem, Name, LinkCountsExact: true, options.IncludeAlternateStreams, volume));
        tree.Aggregate(options.HardlinkMode);
        return new ScanResult(tree, errors, sw.Elapsed);
    }

    private static void QueryExact(Microsoft.Win32.SafeHandles.SafeFileHandle handle, ref EntryData e, ref byte[] streamBuffer, ScanOptions options, out int error)
    {
        bool isDirectory = (e.Flags & NodeFlags.Directory) != 0;
        if (!FileQueries.TryGetStandardInfo(handle, out var std, out error))
        {
            return;
        }

        // Directories: NTFS reports the index allocation as both EndOfFile and AllocationSize.
        // Decision: it counts toward Allocated only, never toward the logical size.
        e.LinkCount = isDirectory ? 0 : (int)std.NumberOfLinks;
        e.Size = isDirectory ? 0 : std.EndOfFile;
        e.Allocated = std.AllocationSize;

        if (EntryClassifier.NeedsCompressedSize(e.Flags))
        {
            if (!FileQueries.TryGetCompressedAllocation(handle, e.Size, out long compressed, out error))
            {
                return;
            }

            e.Allocated = compressed;
        }

        if (options.IncludeAlternateStreams)
        {
            if (!FileQueries.TryGetStreams(handle, ref streamBuffer, out var streams, out error))
            {
                return;
            }

            e.Size += streams.AlternateSize;
            e.Allocated += streams.AlternateAllocated;
            if (streams.AlternateCount > 0)
            {
                e.Flags |= NodeFlags.AlternateStreams;
            }
        }
    }
}
