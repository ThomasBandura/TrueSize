using System.Diagnostics;
using TrueSize.Core.Model;
using TrueSize.Core.Scanning;

// Usage: see TrueSize.Benchmarks.csproj.
string path = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
bool withReference = args.Contains("--reference");

if (args.Contains("--bdn"))
{
    Environment.SetEnvironmentVariable("TRUESIZE_BENCH_PATH", path);
    BenchmarkDotNet.Running.BenchmarkRunner.Run<TrueSize.Benchmarks.ScannerBenchmarks>();
    return;
}

if (args.Contains("--usn-probe"))
{
    TrueSize.Benchmarks.UsnProbe.Run(path, args.Contains("--enum-only"));
    return;
}

if (args.Contains("--tags"))
{
    // Histogram of reparse tags by tag and by the first two path levels below the scan root.
    var tree = new DirectoryScanner().Scan(path, new ScanOptions { ResolveLinkCounts = false }).Tree;
    var byTag = new Dictionary<uint, int>();
    var byPlace = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (int i = 1; i < tree.Count; i++)
    {
        if (!tree.TryGetReparseTag(i, out uint tag))
        {
            continue;
        }

        byTag[tag] = byTag.GetValueOrDefault(tag) + 1;
        var parts = Path.GetRelativePath(path, tree.GetPath(i)).Split('\\');
        string place = string.Join('\\', parts.Take(Math.Min(3, parts.Length - 1)));
        byPlace[place] = byPlace.GetValueOrDefault(place) + 1;
    }

    foreach (var (tag, count) in byTag.OrderByDescending(p => p.Value))
    {
        Console.WriteLine($"tag 0x{tag:X8}: {count:N0}");
    }

    foreach (var (place, count) in byPlace.OrderByDescending(p => p.Value).Take(15))
    {
        Console.WriteLine($"{count,9:N0}  {place}");
    }

    return;
}

if (args.Contains("--mft-profile"))
{
    var vol = VolumeInfo.FromPath(path);
    foreach (bool unbuffered in new[] { true, false })
    {
        var sw = Stopwatch.StartNew();
        using var mft = TrueSize.Core.Ntfs.MftVolume.Open(vol.Root, unbuffered);
        var open = sw.Elapsed;
        long count = 0;
        mft.ReadAllRecords((n, r) => count++, CancellationToken.None);
        double seconds = (sw.Elapsed - open).TotalSeconds;
        double mb = mft.RecordCount * (double)mft.RecordSize / (1024 * 1024);
        Console.WriteLine($"read only, unbuffered={unbuffered}: open {open.TotalMilliseconds:F0} ms, read {seconds * 1000:F0} ms, {mb:F0} MB, {mb / seconds:F0} MB/s, " +
                          $"records {mft.RecordCount:N0} x {mft.RecordSize} B, cluster {mft.ClusterSize}, extents {mft.MftExtents.Count}");
    }

    for (int i = 0; i < 2; i++)
    {
        var result = new NtfsMftScanner().Scan(path, new ScanOptions());
        var t = NtfsMftScanner.LastTimings;
        Console.WriteLine($"full scan {result.Duration.TotalMilliseconds:F0} ms: open {t.Open.TotalMilliseconds:F0}, read+parse {t.ReadAndParse.TotalMilliseconds:F0}, " +
                          $"index+reparse {t.ChildIndexAndReparse.TotalMilliseconds:F0}, tree {t.TreeAndAggregate.TotalMilliseconds:F0} ms; nodes {result.Tree.Count:N0}; nonresident reparse {t.NonResidentReparseRead:N0} read of {t.NonResidentReparseTotal:N0}; FILE_NAME tag mismatches {t.FileNameTagMismatches}");
    }

    return;
}

if (args.Contains("--probe"))
{
    Probe(path);
    return;
}

static unsafe void Probe(string target)
{
    string ext = TrueSize.Core.Interop.FileQueries.ToExtendedPath(target) + "\0";
    fixed (char* p = ext)
    {
        bool ok = TrueSize.Core.Interop.FileQueries.TryStatByName(p, out var st, out int err);
        Console.WriteLine($"ByName: ok={ok} err={err} alloc={st.AllocationSize} eof={st.EndOfFile} attrs=0x{st.FileAttributes:x} links={st.NumberOfLinks}");
        using var h = TrueSize.Core.Interop.FileQueries.OpenForAttributes(p);
        TrueSize.Core.Interop.FileQueries.TryGetStandardInfo(h, out var std, out err);
        Console.WriteLine($"Handle: alloc={std.AllocationSize} eof={std.EndOfFile} links={std.NumberOfLinks} err={err}");
        bool cok = TrueSize.Core.Interop.FileQueries.TryGetCompressedSize(h, out long compressed, out err);
        Console.WriteLine($"Compression: ok={cok} compressedSize={compressed} err={err} attrs={File.GetAttributes(target)}");
    }
}

if (args.Contains("--diff"))
{
    // Lists nodes whose own Size/Allocated differ between DirectoryScanner and ReferenceScanner.
    var fast = new DirectoryScanner().Scan(path, new ScanOptions()).Tree;
    var reference = new ReferenceScanner().Scan(path, new ScanOptions()).Tree;
    var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < reference.Count; i++)
    {
        byPath[reference.GetPath(i)] = i;
    }

    for (int i = 0; i < fast.Count; i++)
    {
        string p = fast.GetPath(i);
        if (!byPath.TryGetValue(p, out int r))
        {
            Console.WriteLine($"only fast: {p}");
        }
        else if (fast.GetSize(i) != reference.GetSize(r) || fast.GetAllocated(i) != reference.GetAllocated(r))
        {
            Console.WriteLine($"{p}: size {fast.GetSize(i)} vs {reference.GetSize(r)}, alloc {fast.GetAllocated(i)} vs {reference.GetAllocated(r)}, flags {fast.GetFlags(i)}");
        }
    }

    return;
}

if (!args.Contains("--mft-only"))
{
    Run("Directory (link counts)", new DirectoryScanner(), new ScanOptions());
    Run("Directory (no link counts)", new DirectoryScanner(), new ScanOptions { ResolveLinkCounts = false });
}

if (args.Contains("--single-thread"))
{
    // Very slow on whole volumes (about 4x the parallel scan); only for measuring parallel speed-up.
    Run("Directory (link counts, 1 thread)", new DirectoryScanner(), new ScanOptions { MaxParallelism = 1 });
}
if (ScannerSelector.IsElevated() && VolumeInfo.FromPath(path).FileSystem == "NTFS")
{
    Run("NtfsMft", new NtfsMftScanner(), new ScanOptions());
}

if (withReference)
{
    Run("Reference", new ReferenceScanner(), new ScanOptions());
}

void Run(string label, IVolumeScanner scanner, ScanOptions options)
{
    // Warm-up pass so the file system cache state is comparable between variants.
    scanner.Scan(path, options);
    var sw = Stopwatch.StartNew();
    var result = scanner.Scan(path, options);
    sw.Stop();
    var t = result.Tree;
    long dirAllocated = 0;
    for (int i = 0; i < t.Count; i++)
    {
        if ((t.GetFlags(i) & NodeFlags.Directory) != 0)
        {
            dirAllocated += t.GetAllocated(i);
        }
    }

    Console.WriteLine(
        $"{label,-36} {sw.Elapsed.TotalMilliseconds,9:F0} ms  files={t.GetFileCount(0),9:N0} dirs={t.GetDirectoryCount(0),8:N0} " +
        $"size={t.GetTotalSize(0),16:N0} alloc={t.GetTotalAllocated(0),16:N0} freeable={t.GetFreeable(0),16:N0} dirAlloc={dirAllocated,12:N0} " +
        $"errors={result.Errors.Count} exactLinks={t.Info.LinkCountsExact} fs={t.Info.FileSystem}");
}
