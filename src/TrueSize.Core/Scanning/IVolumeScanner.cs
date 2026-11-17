using TrueSize.Core.Model;

namespace TrueSize.Core.Scanning;

public sealed record ScanResult(ScanTree Tree, IReadOnlyList<ScanError> Errors, TimeSpan Duration);

public interface IVolumeScanner
{
    string Name { get; }

    /// <summary>Scans <paramref name="path"/> synchronously. The returned tree is already aggregated with <see cref="ScanOptions.HardlinkMode"/>.</summary>
    ScanResult Scan(string path, ScanOptions options, ScanProgress? progress = null, CancellationToken cancellationToken = default);
}
