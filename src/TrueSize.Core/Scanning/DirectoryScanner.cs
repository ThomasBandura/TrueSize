using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using TrueSize.Core.Interop;
using TrueSize.Core.Model;

namespace TrueSize.Core.Scanning;

/// <summary>
/// Scanner without elevation. Enumerates directories in parallel with FileIdExtdDirectoryInfo (sizes, attributes,
/// reparse tag and 128-bit file id per entry without opening files). Link counts and current sizes come from
/// GetFileInformationByName (no handle). Files are only opened when sparse/compressed/WOF/dedup allocation
/// or alternate streams are required. Cloud placeholders are never opened.
/// </summary>
public sealed class DirectoryScanner : IVolumeScanner
{
    public string Name => "Directory";

    public ScanResult Scan(string path, ScanOptions options, ScanProgress? progress = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
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
        var run = new Run(options, progress ?? new ScanProgress(), cancellationToken);
        int rootNode = run.Builder.AddRoot(root, default);
        run.Execute(new WorkItem(rootNode, root, IsRoot: true));

        var tree = run.Builder.Build(new ScanTreeInfo(
            root,
            volume.Root,
            volume.SerialNumber,
            volume.FileSystem,
            Name,
            LinkCountsExact: options.ResolveLinkCounts && run.UnknownLinkCounts == 0,
            AlternateStreamsIncluded: options.IncludeAlternateStreams));
        tree.Aggregate(options.HardlinkMode);
        return new ScanResult(tree, run.Errors.ToArray(), sw.Elapsed);
    }

    private readonly record struct WorkItem(int Node, string Path, bool IsRoot);

    private sealed unsafe class Run(ScanOptions options, ScanProgress progress, CancellationToken cancellationToken)
    {
        private readonly ConcurrentStack<WorkItem> _stack = new();
        private readonly SemaphoreSlim _signal = new(0);
        private int _pending;
        private volatile bool _done;
        private ExceptionDispatchInfo? _fault;
        private int _unknownLinkCounts;
        private int _workerCount;

        public ScanTreeBuilder Builder { get; } = new();

        public ConcurrentQueue<ScanError> Errors { get; } = new();

        public int UnknownLinkCounts => Volatile.Read(ref _unknownLinkCounts);

        public void Execute(WorkItem root)
        {
            int workers = _workerCount = options.MaxParallelism > 0 ? options.MaxParallelism : Environment.ProcessorCount;
            _pending = 1;
            _stack.Push(root);
            _signal.Release();

            var threads = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                threads[i] = new Thread(Worker) { IsBackground = true, Name = $"TrueSize scan {i}" };
                threads[i].Start();
            }

            foreach (var t in threads)
            {
                t.Join();
            }

            _fault?.Throw();
            cancellationToken.ThrowIfCancellationRequested();
        }

        private void Worker()
        {
            using var reader = new DirectoryReader(options.DirectoryBufferSize);
            var paths = new PathBuffer();
            var streamBuffer = new byte[4096];
            var indices = new int[256];

            try
            {
                while (true)
                {
                    _signal.Wait(cancellationToken);
                    if (_done)
                    {
                        return;
                    }

                    if (!_stack.TryPop(out var item))
                    {
                        continue;
                    }

                    try
                    {
                        ProcessDirectory(item, reader, paths, ref streamBuffer, ref indices);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.CompareExchange(ref _fault, ExceptionDispatchInfo.Capture(ex), null);
                        Finish();
                        return;
                    }

                    if (Interlocked.Decrement(ref _pending) == 0)
                    {
                        Finish();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Finish();
            }
        }

        // Wakes every worker so it observes _done and exits.
        private void Finish()
        {
            _done = true;
            _signal.Release(_workerCount);
        }

        private void ProcessDirectory(WorkItem item, DirectoryReader reader, PathBuffer paths, ref byte[] streamBuffer, ref int[] indices)
        {
            paths.SetDirectory(FileQueries.ToExtendedPath(item.Path));
            int listError;
            fixed (char* dir = &paths.Directory)
            {
                using var handle = FileQueries.OpenDirectoryForListing(dir, followReparsePoint: item.IsRoot);
                if (handle.IsInvalid)
                {
                    int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                    AddError(item.Path, error, "OpenDirectory");
                    Builder.SetFlags(item.Node, error == Native.ERROR_ACCESS_DENIED ? NodeFlags.AccessDenied : NodeFlags.Error);

                    // Listing may be denied while attributes are still readable.
                    using var attributes = FileQueries.OpenForAttributes(dir);
                    if (!attributes.IsInvalid && FileQueries.TryGetStandardInfo(attributes, out var deniedInfo, out _))
                    {
                        Builder.SetAllocated(item.Node, deniedInfo.AllocationSize);
                    }

                    return;
                }

                // Directory index allocation. Directory entries and GetFileInformationByName (without an open
                // handle) can report 0 here; the open handle is authoritative.
                if (FileQueries.TryGetStandardInfo(handle, out var directoryInfo, out _))
                {
                    Builder.SetAllocated(item.Node, directoryInfo.AllocationSize);
                }

                listError = reader.Read(handle);
            }

            if (listError != 0)
            {
                AddError(item.Path, listError, "ListDirectory");
                Builder.SetFlags(item.Node, listError == Native.ERROR_ACCESS_DENIED ? NodeFlags.AccessDenied : NodeFlags.Error);
            }

            int count = reader.Count;
            long files = 0, directories = 0, bytes = 0;
            for (int i = 0; i < count; i++)
            {
                ref EntryData e = ref reader.Entries[i];
                Enrich(item.Path, ref e, reader.NameOf(e), paths, ref streamBuffer);
                if ((e.Flags & NodeFlags.Directory) != 0)
                {
                    directories++;
                }
                else
                {
                    files++;
                }

                bytes += e.Allocated;
            }

            if (indices.Length < count)
            {
                indices = new int[Math.Max(count, indices.Length * 2)];
            }

            Builder.AddChildren(item.Node, reader.EntrySpan, reader.Names.AsSpan(0, reader.NamesLength), indices);
            progress.Add(files, directories, bytes);

            int pushed = 0;
            for (int i = 0; i < count; i++)
            {
                ref EntryData e = ref reader.Entries[i];
                if ((e.Flags & NodeFlags.Directory) == 0 || EntryClassifier.ShouldSkipDirectory(e.Flags, options))
                {
                    continue;
                }

                string childPath = Path.Join(item.Path, reader.NameOf(e));
                Interlocked.Increment(ref _pending);
                _stack.Push(new WorkItem(indices[i], childPath, IsRoot: false));
                pushed++;
            }

            if (pushed > 0)
            {
                _signal.Release(pushed);
            }
        }

        private void Enrich(string directoryPath, ref EntryData e, ReadOnlySpan<char> name, PathBuffer paths, ref byte[] streamBuffer)
        {
            bool isDirectory = (e.Flags & NodeFlags.Directory) != 0;
            if (!EntryClassifier.MayOpen(e.Flags))
            {
                // Cloud placeholders: attributes only. They cannot carry hard links in practice.
                e.LinkCount = isDirectory ? 0 : 1;
                return;
            }

            // Directories get their own allocation from their listing handle (see ProcessDirectory).
            // GetFileInformationByName can answer from cached/duplicated directory-entry metadata, so its values are
            // verified with an open handle whenever they could be stale: hard links (entry sizes of other links are
            // not updated) and allocation below the logical size (WOF hides its reparse point and reports 0).
            bool needStandard = false;
            fixed (char* p = &paths.Child(name))
            {
                if (!isDirectory)
                {
                    if (options.ResolveLinkCounts && FileQueries.TryStatByName(p, out var stat, out _))
                    {
                        Apply(ref e, isDirectory, stat.AllocationSize, stat.EndOfFile, stat.NumberOfLinks);
                        needStandard = stat.NumberOfLinks > 1 || IsUnderAllocated(e);
                    }
                    else
                    {
                        needStandard = options.ResolveLinkCounts || IsUnderAllocated(e);
                    }

                    needStandard |= EntryClassifier.NeedsStandardInfo(e.Flags);
                }

                bool needCompressed = EntryClassifier.NeedsCompressedSize(e.Flags);
                bool needStreams = options.IncludeAlternateStreams;
                if (needStandard || needCompressed || needStreams)
                {
                    using var handle = FileQueries.OpenForAttributes(p);
                    if (handle.IsInvalid)
                    {
                        AddError(Path.Join(directoryPath, name), System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "OpenFile");
                        e.Flags |= NodeFlags.Error;
                    }
                    else
                    {
                        int error;
                        if (needStandard && FileQueries.TryGetStandardInfo(handle, out var std, out error))
                        {
                            Apply(ref e, isDirectory, std.AllocationSize, std.EndOfFile, std.NumberOfLinks);
                        }

                        if (needCompressed && FileQueries.TryGetCompressedAllocation(handle, e.Size, out long compressed, out error))
                        {
                            e.Allocated = compressed;
                        }

                        if (needStreams)
                        {
                            if (FileQueries.TryGetStreams(handle, ref streamBuffer, out var streams, out error))
                            {
                                e.Size += streams.AlternateSize;
                                e.Allocated += streams.AlternateAllocated;
                                if (streams.AlternateCount > 0)
                                {
                                    e.Flags |= NodeFlags.AlternateStreams;
                                }
                            }
                            else
                            {
                                AddError(Path.Join(directoryPath, name), error, "QueryStreams");
                            }
                        }
                    }
                }
            }

            if (!isDirectory && options.ResolveLinkCounts && e.LinkCount == 0)
            {
                e.Flags |= NodeFlags.LinkCountUnknown;
                Interlocked.Increment(ref _unknownLinkCounts);
            }
        }

        private const NodeFlags UnderAllocatedByDesign = NodeFlags.Sparse | NodeFlags.Compressed | NodeFlags.CloudNotLocal | NodeFlags.Offline;

        // NTFS resident files (data inside the MFT record, up to ~4 KiB records) legitimately report 0 allocated bytes.
        private const long ResidentLimit = 4096;

        private static bool IsUnderAllocated(in EntryData e) =>
            e.Allocated < e.Size && e.Size > ResidentLimit && (e.Flags & UnderAllocatedByDesign) == 0;

        // Directories: logical size stays 0 (decision: directory index counts toward Allocated only).
        private static void Apply(ref EntryData e, bool isDirectory, long allocated, long endOfFile, uint links)
        {
            e.Allocated = allocated;
            if (!isDirectory)
            {
                e.Size = endOfFile;
                e.LinkCount = (int)links;
            }
        }

        private void AddError(string path, int error, string operation)
        {
            Errors.Enqueue(new ScanError(path, error, operation));
            progress.AddError();
        }
    }
}
