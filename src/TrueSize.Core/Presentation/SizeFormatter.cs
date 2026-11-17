using System.Globalization;
using System.Text;
using TrueSize.Core.Model;

namespace TrueSize.Core.Presentation;

/// <summary>Human-readable sizes, e.g. "1.5 GB" (binary units). Shared by the app and the PowerShell format view.</summary>
public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes) => Format(bytes, CultureInfo.InvariantCulture);

    public static string Format(long bytes, IFormatProvider culture)
    {
        double value = bytes;
        int unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? bytes.ToString(culture) + " B"
            : value.ToString(value >= 100 ? "F0" : "F1", culture) + " " + Units[unit];
    }
}

/// <summary>Short flag markers used by the app and the PowerShell module.</summary>
public static class NodeMarkers
{
    /// <summary>J junction, L symlink, H hard link, X links outside, C cloud, S sparse, Z compressed/WOF, A ADS, ? reparse tag unknown, ! access error.</summary>
    public static string Get(NodeFlags f)
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

    /// <summary>German description of all set markers, for tooltips.</summary>
    public static string Describe(NodeFlags f)
    {
        var parts = new List<string>();
        if ((f & NodeFlags.Junction) != 0) parts.Add("Junction / Bereitstellungspunkt (nicht verfolgt)");
        if ((f & NodeFlags.Symlink) != 0) parts.Add("Symbolischer Link (nicht verfolgt)");
        if ((f & NodeFlags.Hardlink) != 0) parts.Add("Hardlink (nur einmal gezählt)");
        if ((f & NodeFlags.ExternalLinks) != 0) parts.Add("Weitere Hardlinks außerhalb: Löschen gibt keinen Platz frei");
        if ((f & NodeFlags.CloudPlaceholder) != 0) parts.Add("Cloud-Platzhalter");
        if ((f & NodeFlags.CloudNotPopulated) != 0) parts.Add("Cloud-Ordner nicht lokal befüllt");
        if ((f & NodeFlags.Sparse) != 0) parts.Add("Sparse-Datei");
        if ((f & NodeFlags.Compressed) != 0) parts.Add("NTFS-komprimiert");
        if ((f & NodeFlags.Wof) != 0) parts.Add("WOF/CompactOS-komprimiert");
        if ((f & NodeFlags.AlternateStreams) != 0) parts.Add("Alternative Datenströme");
        if ((f & NodeFlags.ReparseTagUnknown) != 0) parts.Add("Reparse Point, Typ nicht gelesen");
        if ((f & NodeFlags.AccessDenied) != 0) parts.Add("Zugriff verweigert");
        if ((f & NodeFlags.Error) != 0) parts.Add("Lesefehler");
        return string.Join(Environment.NewLine, parts);
    }
}
