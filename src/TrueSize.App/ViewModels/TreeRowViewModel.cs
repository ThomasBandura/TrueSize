using System.Globalization;
using TrueSize.Core.Model;
using TrueSize.Core.Presentation;

namespace TrueSize.App.ViewModels;

/// <summary>
/// Display data for one visible row. Created on demand by <see cref="RowCollection"/> for realized rows only, so a
/// directory with 100,000 entries does not create 100,000 objects.
/// </summary>
public sealed class TreeRowViewModel
{
    private const string ChevronRight = "";
    private const string ChevronDown = "";
    private const string FolderGlyph = "";
    private const string FileGlyph = "";
    private const string LinkGlyph = "";

    internal TreeRowViewModel(FlatTreeModel model, int rowIndex)
    {
        var tree = model.Tree;
        var row = model.Rows[rowIndex];
        var flags = tree.GetFlags(row.Node);
        var culture = CultureInfo.CurrentCulture;
        long rootAllocated = tree.GetTotalAllocated(ScanTree.Root);

        RowIndex = rowIndex;
        Node = row.Node;
        Name = tree.GetName(row.Node).ToString();
        Indent = new Thickness(row.Depth * 20, 0, 0, 0);
        IsDirectory = (flags & NodeFlags.Directory) != 0;
        CanExpand = model.CanExpand(row.Node);
        Chevron = CanExpand ? (model.IsExpanded(row.Node) ? ChevronDown : ChevronRight) : string.Empty;
        Icon = (flags & (NodeFlags.Junction | NodeFlags.Symlink)) != 0 ? LinkGlyph : IsDirectory ? FolderGlyph : FileGlyph;
        SizeText = SizeFormatter.Format(tree.GetTotalSize(row.Node), culture);
        AllocatedText = SizeFormatter.Format(tree.GetTotalAllocated(row.Node), culture);
        FreeableText = SizeFormatter.Format(tree.GetFreeable(row.Node), culture);
        FilesText = IsDirectory ? tree.GetFileCount(row.Node).ToString("N0", culture) : string.Empty;
        Percent = rootAllocated > 0 ? 100.0 * tree.GetTotalAllocated(row.Node) / rootAllocated : 0;
        PercentText = Percent.ToString("F1", culture) + " %";
        Markers = NodeMarkers.Get(flags);
        MarkersDescription = NodeMarkers.Describe(flags);
        _fullPath = new Lazy<string>(() => tree.GetPath(row.Node));
    }

    private readonly Lazy<string> _fullPath;

    public int RowIndex { get; }

    public int Node { get; }

    public string Name { get; }

    public Thickness Indent { get; }

    public bool IsDirectory { get; }

    public bool CanExpand { get; }

    public string Chevron { get; }

    public string Icon { get; }

    public string SizeText { get; }

    public string AllocatedText { get; }

    public string FreeableText { get; }

    public string FilesText { get; }

    public double Percent { get; }

    public string PercentText { get; }

    public string Markers { get; }

    public string MarkersDescription { get; }

    public string FullPath => _fullPath.Value;

    /// <summary>Accessible name: the list is read by screen readers row by row.</summary>
    public string AutomationName => $"{Name}, belegt {AllocatedText}, Größe {SizeText}, {PercentText}";
}
