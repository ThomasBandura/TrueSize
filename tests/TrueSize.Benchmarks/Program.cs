using System.Diagnostics;
using TrueSize.Core.Model;
using TrueSize.Core.Scanning;

// Phase 1 quick timing tool (BenchmarkDotNet suite follows in phase 3).
// Usage: TrueSize.Benchmarks <path> [--reference]
string path = args.Length > 0 ? args[0] : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
bool withReference = args.Contains("--reference");

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

Run("Directory (link counts)", new DirectoryScanner(), new ScanOptions());
Run("Directory (no link counts)", new DirectoryScanner(), new ScanOptions { ResolveLinkCounts = false });
Run("Directory (link counts, 1 thread)", new DirectoryScanner(), new ScanOptions { MaxParallelism = 1 });
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
