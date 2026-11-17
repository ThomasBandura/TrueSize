using TrueSize.Core.Interop;
using TrueSize.Core.Model;

namespace TrueSize.Core.Scanning;

internal static class EntryClassifier
{
    public static NodeFlags Classify(uint attributes, uint reparseTag)
    {
        var flags = NodeFlags.None;
        bool isDirectory = (attributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
        if (isDirectory)
        {
            flags |= NodeFlags.Directory;
        }

        if ((attributes & Native.FILE_ATTRIBUTE_SPARSE_FILE) != 0)
        {
            flags |= NodeFlags.Sparse;
        }

        if ((attributes & Native.FILE_ATTRIBUTE_COMPRESSED) != 0)
        {
            flags |= NodeFlags.Compressed;
        }

        if ((attributes & Native.FILE_ATTRIBUTE_OFFLINE) != 0)
        {
            flags |= NodeFlags.Offline;
        }

        if ((attributes & Native.FILE_ATTRIBUTE_ENCRYPTED) != 0)
        {
            flags |= NodeFlags.Encrypted;
        }

        if ((attributes & Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0)
        {
            flags |= NodeFlags.CloudNotLocal;
        }

        if ((attributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            flags |= NodeFlags.ReparsePoint;
            switch (reparseTag)
            {
                case Native.IO_REPARSE_TAG_SYMLINK:
                    flags |= NodeFlags.Symlink;
                    break;
                case Native.IO_REPARSE_TAG_MOUNT_POINT:
                    flags |= NodeFlags.Junction;
                    break;
                case Native.IO_REPARSE_TAG_WOF:
                    flags |= NodeFlags.Wof;
                    break;
                case Native.IO_REPARSE_TAG_DEDUP:
                    flags |= NodeFlags.Dedup;
                    break;
            }

            if (Native.IsCloudTag(reparseTag))
            {
                flags |= NodeFlags.CloudPlaceholder;

                // FILE_ATTRIBUTE_RECALL_ON_OPEN shares its value with FILE_ATTRIBUTE_EA, so only trust it on cloud directories.
                if (isDirectory && (attributes & Native.FILE_ATTRIBUTE_RECALL_ON_OPEN) != 0)
                {
                    flags |= NodeFlags.CloudNotPopulated;
                }
            }

            if (isDirectory && Native.IsReparseTagNameSurrogate(reparseTag))
            {
                flags |= NodeFlags.NotFollowed;
            }
        }

        return flags;
    }

    /// <summary>True if the directory must not be enumerated (link target or unpopulated cloud directory).</summary>
    public static bool ShouldSkipDirectory(NodeFlags flags, ScanOptions options) =>
        (flags & NodeFlags.NotFollowed) != 0 ||
        ((flags & NodeFlags.CloudNotPopulated) != 0 && !options.EnumerateCloudDirectories);

    /// <summary>
    /// Files whose real allocation is FileCompressionInfo.CompressedFileSize: NTFS-compressed, WOF and dedup files.
    /// Not sparse files: for a mostly allocated sparse file CompressedFileSize returns the logical size (measured:
    /// 440736 for 458752 allocated bytes), while FileStandardInfo.AllocationSize matches the clusters in use.
    /// </summary>
    public static bool NeedsCompressedSize(NodeFlags flags) =>
        (flags & (NodeFlags.Compressed | NodeFlags.Wof | NodeFlags.Dedup)) != 0 &&
        (flags & (NodeFlags.Directory | NodeFlags.CloudPlaceholder)) == 0;

    /// <summary>Sparse files: the directory entry allocation may be stale, FileStandardInfo via a handle is exact.</summary>
    public static bool NeedsStandardInfo(NodeFlags flags) =>
        (flags & NodeFlags.Sparse) != 0 && !NeedsCompressedSize(flags) &&
        (flags & (NodeFlags.Directory | NodeFlags.CloudPlaceholder)) == 0;

    /// <summary>Cloud placeholders are never opened to avoid any provider interaction.</summary>
    public static bool MayOpen(NodeFlags flags) => (flags & NodeFlags.CloudPlaceholder) == 0;
}
