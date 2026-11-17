namespace TrueSize.Core.Model;

[Flags]
public enum NodeFlags : uint
{
    None = 0,
    Directory = 1u << 0,
    ReparsePoint = 1u << 1,
    /// <summary>Symbolic link (IO_REPARSE_TAG_SYMLINK).</summary>
    Symlink = 1u << 2,
    /// <summary>Junction or volume mount point (IO_REPARSE_TAG_MOUNT_POINT).</summary>
    Junction = 1u << 3,
    /// <summary>Directory that was intentionally not enumerated (name surrogate or unpopulated cloud directory).</summary>
    NotFollowed = 1u << 4,
    /// <summary>Cloud Files placeholder (IO_REPARSE_TAG_CLOUD_*).</summary>
    CloudPlaceholder = 1u << 5,
    /// <summary>Cloud directory with FILE_ATTRIBUTE_RECALL_ON_OPEN; enumerating would contact the provider.</summary>
    CloudNotPopulated = 1u << 6,
    /// <summary>File content not stored locally (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS).</summary>
    CloudNotLocal = 1u << 7,
    Sparse = 1u << 8,
    Compressed = 1u << 9,
    /// <summary>Windows Overlay Filter compressed file (CompactOS / compact /exe).</summary>
    Wof = 1u << 10,
    /// <summary>Data Deduplication reparse point; physical size is shared and not attributable.</summary>
    Dedup = 1u << 11,
    /// <summary>File has more than one hard link (or a duplicate FileId was seen in scope).</summary>
    Hardlink = 1u << 12,
    /// <summary>At least one hard link of this file lies outside the scanned tree. Deleting does not free space.</summary>
    ExternalLinks = 1u << 13,
    /// <summary>Hard link occurrence that is not the primary one (FirstFound mode contributes 0).</summary>
    HardlinkSecondary = 1u << 14,
    AlternateStreams = 1u << 15,
    AccessDenied = 1u << 16,
    Error = 1u << 17,
    /// <summary>Total link count could not be determined; hard link detection is limited to the scanned scope.</summary>
    LinkCountUnknown = 1u << 18,
    Offline = 1u << 19,
    Encrypted = 1u << 20,

    /// <summary>
    /// Reparse point whose tag was not read (MFT scanner, nonresident reparse data, ScanOptions.ResolveAllReparseTags off).
    /// Sizes are unaffected; only tag-derived flags such as Symlink or CloudPlaceholder may be missing.
    /// </summary>
    ReparseTagUnknown = 1u << 21,
}
