using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using TrueSize.Core.Interop;
using TrueSize.Core.Model;
using TrueSize.Core.Scanning;

namespace TrueSize.Benchmarks;

/// <summary>
/// Phase 4: does FSCTL_ENUM_USN_DATA work on this volume (ReFS), and is "USN enumeration + OpenFileById per file"
/// faster than the DirectoryScanner? Prints facts only. Requires elevation.
/// </summary>
internal static unsafe class UsnProbe
{
    public static void Run(string path, bool enumerateOnly = false)
    {
        var volume = VolumeInfo.FromPath(path);
        Console.WriteLine($"volume {volume.Root} fs={volume.FileSystem} serial={volume.SerialNumber:X8} elevated={ScannerSelector.IsElevated()}");

        using var device = UsnEnumerator.OpenVolume(volume.Root);
        var entries = new List<UsnEntry>();
        var versions = new Dictionary<int, int>();
        var sw = Stopwatch.StartNew();
        int error = UsnEnumerator.Enumerate(device, e =>
        {
            entries.Add(e);
            versions[e.MajorVersion] = versions.GetValueOrDefault(e.MajorVersion) + 1;
        });
        var enumTime = sw.Elapsed;
        Console.WriteLine($"FSCTL_ENUM_USN_DATA: error={error} ({(error == 0 ? "OK" : new Win32Exception(error).Message)}), records={entries.Count:N0}, " +
                          $"versions={string.Join(",", versions.Select(v => $"V{v.Key}:{v.Value:N0}"))}, time={enumTime.TotalMilliseconds:F0} ms");
        if (error != 0 || entries.Count == 0)
        {
            return;
        }

        int dirs = entries.Count(e => (e.Attributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0);
        Console.WriteLine($"  directories={dirs:N0}, files={entries.Count - dirs:N0}, high64 ids non-zero={entries.Count(e => e.Id.High != 0):N0}");
        foreach (var e in entries.Take(3))
        {
            Console.WriteLine($"  sample: id={e.Id} parent={e.Parent} attrs=0x{e.Attributes:X} name={e.Name}");
        }

        if (enumerateOnly)
        {
            return;
        }

        // Size lookup per file: OpenFileById + FileStandardInfo (sizes are not part of USN records).
        string rootExtended = FileQueries.ToExtendedPath(volume.Root) + "\0";
        Microsoft.Win32.SafeHandles.SafeFileHandle hint;
        fixed (char* p = rootExtended)
        {
            hint = FileQueries.OpenDirectoryForListing(p, followReparsePoint: true);
        }

        using (hint)
        {
            var files = entries.Where(e => (e.Attributes & Native.FILE_ATTRIBUTE_DIRECTORY) == 0).ToList();
            int sample = Math.Min(20_000, files.Count);
            int failed = 0;
            sw.Restart();
            for (int i = 0; i < sample; i++)
            {
                using var h = UsnEnumerator.OpenById(hint, files[i].Id);
                if (h.IsInvalid || !FileQueries.TryGetStandardInfo(h, out _, out _))
                {
                    failed++;
                }
            }

            double perFileUs = sw.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, sample);
            Console.WriteLine($"OpenFileById sequential: {sample:N0} files, {perFileUs:F1} us/file, failed={failed:N0}, extrapolated all files={perFileUs * files.Count / 1e6:F2} s");

            long allocated = 0, size = 0;
            var errors = new ConcurrentDictionary<int, int>();
            sw.Restart();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, e =>
            {
                using var h = UsnEnumerator.OpenById(hint, e.Id);
                if (h.IsInvalid)
                {
                    errors.AddOrUpdate(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), 1, (_, c) => c + 1);
                    return;
                }

                if (FileQueries.TryGetStandardInfo(h, out var std, out _))
                {
                    Interlocked.Add(ref allocated, std.AllocationSize);
                    Interlocked.Add(ref size, std.EndOfFile);
                }
            });
            var lookupTime = sw.Elapsed;
            Console.WriteLine($"OpenFileById parallel ({Environment.ProcessorCount}): {files.Count:N0} files in {lookupTime.TotalMilliseconds:F0} ms, " +
                              $"size={size:N0} alloc={allocated:N0}, open errors={string.Join(",", errors.Select(x => $"{x.Key}x{x.Value}"))}");
            Console.WriteLine($"USN total (enum + parallel lookup): {(enumTime + lookupTime).TotalMilliseconds:F0} ms");
        }

        // Comparison with the DirectoryScanner on the same volume root.
        foreach (bool links in new[] { true, false })
        {
            sw.Restart();
            var result = new DirectoryScanner().Scan(volume.Root, new ScanOptions { ResolveLinkCounts = links });
            var tree = result.Tree;
            var ids = new HashSet<FileId128>();
            for (int i = 1; i < tree.Count; i++)
            {
                ids.Add(tree.GetFileId(i));
            }

            int matched = entries.Count(e => ids.Contains(e.Id));
            Console.WriteLine($"DirectoryScanner (link counts={links}): {sw.Elapsed.TotalMilliseconds:F0} ms, files={tree.GetFileCount(0):N0} dirs={tree.GetDirectoryCount(0):N0} " +
                              $"size={tree.GetTotalSize(0):N0} alloc={tree.GetTotalAllocated(0):N0} errors={result.Errors.Count}; USN ids found in tree: {matched:N0}/{entries.Count:N0}");
        }
    }
}
