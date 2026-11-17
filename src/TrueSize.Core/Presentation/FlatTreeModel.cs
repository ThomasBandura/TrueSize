using TrueSize.Core.Model;

namespace TrueSize.Core.Presentation;

public enum TreeSortKey
{
    Allocated,
    Size,
    Freeable,
    Files,
    Name,
}

/// <summary>One visible row: tree node and its indentation depth.</summary>
public readonly record struct TreeRow(int Node, int Depth);

/// <summary>
/// Flattened, lazily expanded view of a <see cref="ScanTree"/> for virtualized lists (no TreeView). Only the visible
/// rows exist; expanding a directory inserts its sorted children directly after it. UI-independent and unit-tested.
/// </summary>
public sealed class FlatTreeModel
{
    private readonly List<TreeRow> _rows = [];
    private readonly HashSet<int> _expanded = [];

    public FlatTreeModel(ScanTree tree, TreeSortKey sortKey = TreeSortKey.Allocated)
    {
        Tree = tree;
        SortKey = sortKey;
        _rows.Add(new TreeRow(ScanTree.Root, 0));
        Expand(0);
    }

    public ScanTree Tree { get; }

    public TreeSortKey SortKey { get; private set; }

    public IReadOnlyList<TreeRow> Rows => _rows;

    public bool IsExpanded(int node) => _expanded.Contains(node);

    /// <summary>Directories with at least one child (junctions and other not-followed directories have none).</summary>
    public bool CanExpand(int node) => Tree.GetFirstChild(node) >= 0;

    /// <summary>Inserts the children of the row's node. Returns the number of inserted rows.</summary>
    public int Expand(int rowIndex)
    {
        var row = _rows[rowIndex];
        if (!CanExpand(row.Node) || !_expanded.Add(row.Node))
        {
            return 0;
        }

        var children = SortedChildren(row.Node);
        var inserted = new TreeRow[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            inserted[i] = new TreeRow(children[i], row.Depth + 1);
        }

        _rows.InsertRange(rowIndex + 1, inserted);
        return inserted.Length;
    }

    /// <summary>Removes all descendant rows of the row's node. Returns the number of removed rows.</summary>
    public int Collapse(int rowIndex)
    {
        var row = _rows[rowIndex];
        if (!_expanded.Remove(row.Node))
        {
            return 0;
        }

        int end = rowIndex + 1;
        while (end < _rows.Count && _rows[end].Depth > row.Depth)
        {
            _expanded.Remove(_rows[end].Node);
            end++;
        }

        int count = end - rowIndex - 1;
        _rows.RemoveRange(rowIndex + 1, count);
        return count;
    }

    public int Toggle(int rowIndex) => IsExpanded(_rows[rowIndex].Node) ? -Collapse(rowIndex) : Expand(rowIndex);

    /// <summary>Re-sorts all expanded levels, keeping the expansion state.</summary>
    public void Sort(TreeSortKey key)
    {
        SortKey = key;
        var expanded = new HashSet<int>(_expanded);
        _expanded.Clear();
        _rows.Clear();
        _rows.Add(new TreeRow(ScanTree.Root, 0));
        for (int i = 0; i < _rows.Count; i++)
        {
            if (expanded.Contains(_rows[i].Node))
            {
                Expand(i);
            }
        }
    }

    /// <summary>Row index of a node, or -1 if it is not visible.</summary>
    public int IndexOf(int node) => _rows.FindIndex(r => r.Node == node);

    private List<int> SortedChildren(int node)
    {
        var children = new List<int>();
        foreach (int child in Tree.Children(node))
        {
            children.Add(child);
        }

        children.Sort(Compare);
        return children;
    }

    private int Compare(int a, int b)
    {
        int c = SortKey switch
        {
            TreeSortKey.Size => Tree.GetTotalSize(b).CompareTo(Tree.GetTotalSize(a)),
            TreeSortKey.Freeable => Tree.GetFreeable(b).CompareTo(Tree.GetFreeable(a)),
            TreeSortKey.Files => Tree.GetFileCount(b).CompareTo(Tree.GetFileCount(a)),
            TreeSortKey.Name => 0,
            _ => Tree.GetTotalAllocated(b).CompareTo(Tree.GetTotalAllocated(a)),
        };
        return c != 0 ? c : MemoryExtensions.CompareTo(Tree.GetName(a), Tree.GetName(b), StringComparison.CurrentCultureIgnoreCase);
    }
}
