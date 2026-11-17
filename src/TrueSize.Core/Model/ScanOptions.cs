namespace TrueSize.Core.Model;

public enum HardlinkMode
{
    /// <summary>Count the file once, at the occurrence with the ordinally smallest path.</summary>
    FirstFound,
    /// <summary>Split the size evenly across all occurrences inside the scanned tree.</summary>
    Proportional,
    /// <summary>Exclude hard-linked files from Size/Allocated and report them (once) in the Hardlink columns.</summary>
    Separate,
}

public sealed record ScanOptions
{
    public HardlinkMode HardlinkMode { get; init; } = HardlinkMode.FirstFound;

    /// <summary>Maximum number of directories enumerated concurrently. 0 = Environment.ProcessorCount.</summary>
    public int MaxParallelism { get; init; }

    /// <summary>
    /// Query each file and directory by name (GetFileInformationByName, no handle) for link count, current sizes
    /// and directory index allocation. Required for exact hard link handling, the Freeable metric and NTFS
    /// directory allocation. Directory entry sizes alone can be stale for hard links. Costs roughly 3-7x scan time.
    /// When false, files are still queried if the entry reports less allocation than size (catches WOF).
    /// </summary>
    public bool ResolveLinkCounts { get; init; } = true;

    /// <summary>Open each file to sum alternate data streams. Slow.</summary>
    public bool IncludeAlternateStreams { get; init; }

    /// <summary>Enumerate cloud directories marked RECALL_ON_OPEN. May trigger network activity.</summary>
    public bool EnumerateCloudDirectories { get; init; }

    /// <summary>
    /// MFT scanner: read the reparse tag of every file and directory whose reparse data is stored outside its MFT record
    /// (one cluster read each; ~240k Windows container placeholders on a typical C: cost ~30 s). Off: such nodes are
    /// flagged ReparseTagUnknown. Sizes and the set of nodes do not depend on it.
    /// </summary>
    public bool ResolveAllReparseTags { get; init; }

    /// <summary>Buffer size for directory enumeration.</summary>
    public int DirectoryBufferSize { get; init; } = 64 * 1024;
}
