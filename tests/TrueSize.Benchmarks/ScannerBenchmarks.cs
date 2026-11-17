using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using TrueSize.Core.Model;
using TrueSize.Core.Scanning;

namespace TrueSize.Benchmarks;

/// <summary>
/// Scans one path with each scanner. Runs in-process so an elevated benchmark process keeps its rights.
/// Path: environment variable TRUESIZE_BENCH_PATH (default: Windows directory).
/// Note: the MFT scanner always reads the whole $MFT, independent of the path.
/// </summary>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class ScannerBenchmarks
{
    private string _path = "";

    [GlobalSetup]
    public void Setup() =>
        _path = Environment.GetEnvironmentVariable("TRUESIZE_BENCH_PATH") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Benchmark(Baseline = true)]
    public long Directory_LinkCounts() => new DirectoryScanner().Scan(_path, new ScanOptions()).Tree.GetTotalAllocated(0);

    [Benchmark]
    public long Directory_NoLinkCounts() => new DirectoryScanner().Scan(_path, new ScanOptions { ResolveLinkCounts = false }).Tree.GetTotalAllocated(0);

    [Benchmark]
    public long NtfsMft()
    {
        if (!ScannerSelector.IsElevated())
        {
            throw new InvalidOperationException("The MFT benchmark requires an elevated process.");
        }

        return new NtfsMftScanner().Scan(_path, new ScanOptions()).Tree.GetTotalAllocated(0);
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithLaunchCount(1)
                .WithWarmupCount(1)
                .WithIterationCount(5));
            WithOptions(ConfigOptions.DisableOptimizationsValidator);
        }
    }
}
