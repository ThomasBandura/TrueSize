using System.Globalization;
using System.Text;
using TrueSize.Core.Model;

namespace TrueSize.PowerShell;

/// <summary>One output row of Get-TrueSize. All sizes are aggregated over the subtree.</summary>
public sealed class TrueSizeItem
{
    public required string Name { get; init; }

    /// <summary>Indented name (tree output) or path relative to the scan root (flat output).</summary>
    public required string DisplayName { get; init; }

    public required string FullName { get; init; }

    public required int Depth { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>Logical size (sum of stream lengths).</summary>
    public required long Size { get; init; }

    /// <summary>Allocated bytes on disk, including NTFS directory indexes.</summary>
    public required long Allocated { get; init; }

    /// <summary>Allocated bytes released by deleting this item (hard links outside it are not freed).</summary>
    public required long Freeable { get; init; }

    /// <summary>Hard-linked data reported separately (HardlinkMode Separate only).</summary>
    public required long HardlinkAllocated { get; init; }

    /// <summary>Share of the scan root's allocated bytes, in percent.</summary>
    public required double Percent { get; init; }

    public required long Files { get; init; }

    public required long Directories { get; init; }

    public required int LinkCount { get; init; }

    public required NodeFlags Flags { get; init; }

    /// <summary>Short markers: D dir, J junction, L symlink, H hard link, X links outside, C cloud, S sparse, Z compressed, A ADS, ? reparse tag unknown, ! access error.</summary>
    public string Markers => GetMarkers(Flags);

    public override string ToString() => FullName;

    internal static string GetMarkers(NodeFlags f)
    {
        var sb = new StringBuilder(8);
        Add('J', NodeFlags.Junction);
        Add('L', NodeFlags.Symlink);
        Add('H', NodeFlags.Hardlink);
        Add('X', NodeFlags.ExternalLinks);
        Add('C', NodeFlags.CloudPlaceholder);
        Add('S', NodeFlags.Sparse);
        Add('Z', NodeFlags.Compressed | NodeFlags.Wof);
        Add('A', NodeFlags.AlternateStreams);
        Add('?', NodeFlags.ReparseTagUnknown);
        Add('!', NodeFlags.AccessDenied | NodeFlags.Error);
        return sb.ToString();

        void Add(char c, NodeFlags mask)
        {
            if ((f & mask) != 0)
            {
                sb.Append(c);
            }
        }
    }
}

/// <summary>Human-readable sizes for the format view, e.g. "1.5 GB" (binary units, invariant culture).</summary>
public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
            : value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
