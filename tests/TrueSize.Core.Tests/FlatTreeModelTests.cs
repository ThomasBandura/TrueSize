using TrueSize.Core.Model;
using TrueSize.Core.Presentation;

namespace TrueSize.Core.Tests;

public sealed class FlatTreeModelTests
{
    // root
    // ├── big   (dir, 300)  ├── b1 200  └── b2 100
    // ├── small (dir, 10)   └── s1 10
    // ├── empty (dir, no children)
    // └── file  (50)
    private static ScanTree Build()
    {
        var builder = new ScanTreeBuilder();
        int root = builder.AddRoot(@"X:\root", default);
        var top = Add(builder, root, ("big", true, 0), ("small", true, 0), ("empty", true, 0), ("file", false, 50));
        Add(builder, top[0], ("b1", false, 200), ("b2", false, 100));
        Add(builder, top[1], ("s1", false, 10));
        var tree = builder.Build(new ScanTreeInfo(@"X:\root", @"X:\", 1, "NTFS", "test", true, false));
        tree.Aggregate(HardlinkMode.FirstFound);
        return tree;
    }

    private static int[] Add(ScanTreeBuilder b, int parent, params (string Name, bool Dir, long Size)[] entries)
    {
        string names = string.Concat(entries.Select(e => e.Name));
        var data = new EntryData[entries.Length];
        int offset = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            data[i] = new EntryData
            {
                Flags = entries[i].Dir ? NodeFlags.Directory : NodeFlags.None,
                Size = entries[i].Size,
                Allocated = entries[i].Size,
                LinkCount = entries[i].Dir ? 0 : 1,
                NameStart = offset,
                NameLength = entries[i].Name.Length,
            };
            offset += entries[i].Name.Length;
        }

        var indices = new int[entries.Length];
        b.AddChildren(parent, data, names, indices);
        return indices;
    }

    private static string[] Names(FlatTreeModel m) => m.Rows.Select(r => new string(' ', r.Depth) + m.Tree.GetName(r.Node).ToString()).ToArray();

    [Fact]
    public void Initially_RootIsExpanded_ChildrenSortedByAllocated()
    {
        var m = new FlatTreeModel(Build());
        Assert.Equal([@"X:\root", " big", " file", " small", " empty"], Names(m));
        Assert.True(m.IsExpanded(ScanTree.Root));
    }

    [Fact]
    public void Expand_InsertsSortedChildrenAfterRow_AndCollapseRemovesThem()
    {
        var m = new FlatTreeModel(Build());
        Assert.Equal(2, m.Expand(1));
        Assert.Equal([@"X:\root", " big", "  b1", "  b2", " file", " small", " empty"], Names(m));

        Assert.Equal(2, m.Collapse(1));
        Assert.Equal([@"X:\root", " big", " file", " small", " empty"], Names(m));
    }

    [Fact]
    public void CollapsingAParent_RemovesExpandedGrandchildren()
    {
        var m = new FlatTreeModel(Build());
        m.Expand(1);   // big
        m.Expand(5);   // small (index after big's two children and file)
        Assert.Equal(8, m.Rows.Count);

        m.Collapse(0); // root
        Assert.Single(m.Rows);
        Assert.False(m.IsExpanded(m.Tree.Find("big")));
    }

    [Fact]
    public void EmptyDirectoriesAndFiles_CannotExpand()
    {
        var m = new FlatTreeModel(Build());
        int empty = m.IndexOf(m.Tree.Find("empty"));
        int file = m.IndexOf(m.Tree.Find("file"));
        Assert.False(m.CanExpand(m.Rows[empty].Node));
        Assert.Equal(0, m.Expand(empty));
        Assert.Equal(0, m.Expand(file));
    }

    [Fact]
    public void Sort_KeepsExpansionState()
    {
        var m = new FlatTreeModel(Build());
        m.Expand(1); // big
        m.Sort(TreeSortKey.Name);
        Assert.Equal([@"X:\root", " big", "  b1", "  b2", " empty", " file", " small"], Names(m));
        Assert.Equal(TreeSortKey.Name, m.SortKey);
    }

    [Fact]
    public void Toggle_ReturnsSignedRowDelta()
    {
        var m = new FlatTreeModel(Build());
        Assert.Equal(2, m.Toggle(1));
        Assert.Equal(-2, m.Toggle(1));
    }

    [Fact]
    public void SizeFormatter_UsesBinaryUnits()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("512 B", SizeFormatter.Format(512, invariant));
        Assert.Equal("1.5 KB", SizeFormatter.Format(1536, invariant));
        Assert.Equal("100 MB", SizeFormatter.Format(100L * 1024 * 1024, invariant));
    }
}
