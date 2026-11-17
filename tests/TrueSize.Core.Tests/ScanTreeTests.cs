using TrueSize.Core.Model;

namespace TrueSize.Core.Tests;

public sealed class ScanTreeTests
{
    private static ScanTree Build(Action<ScanTreeBuilder, int> fill)
    {
        var builder = new ScanTreeBuilder();
        int root = builder.AddRoot(@"X:\root", default);
        fill(builder, root);
        var tree = builder.Build(new ScanTreeInfo(@"X:\root", @"X:\", 1, "NTFS", "test", true, false));
        tree.Aggregate(HardlinkMode.FirstFound);
        return tree;
    }

    private static void Add(ScanTreeBuilder b, int parent, params (string Name, NodeFlags Flags, long Size, ulong Id, int Links)[] entries)
    {
        string names = string.Concat(entries.Select(e => e.Name));
        var data = new EntryData[entries.Length];
        int offset = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            data[i] = new EntryData
            {
                Flags = entries[i].Flags,
                Size = entries[i].Size,
                Allocated = entries[i].Size,
                FileId = new FileId128(entries[i].Id, 0),
                LinkCount = entries[i].Links,
                NameStart = offset,
                NameLength = entries[i].Name.Length,
            };
            offset += entries[i].Name.Length;
        }

        b.AddChildren(parent, data, names, new int[entries.Length]);
    }

    [Fact]
    public void Aggregation_SumsBottomUp_AndCounts()
    {
        var tree = Build((b, root) =>
        {
            Add(b, root, ("a", NodeFlags.Directory, 0, 1, 1), ("f1", NodeFlags.None, 100, 2, 1));
            Add(b, 1, ("f2", NodeFlags.None, 50, 3, 1), ("sub", NodeFlags.Directory, 0, 4, 1));
            Add(b, 4, ("f3", NodeFlags.None, 7, 5, 1));
        });

        Assert.Equal(157, tree.GetTotalSize(ScanTree.Root));
        Assert.Equal(57, tree.GetTotalSize(tree.Find("a")));
        Assert.Equal(3, tree.GetFileCount(ScanTree.Root));
        Assert.Equal(2, tree.GetDirectoryCount(ScanTree.Root));
        Assert.Equal(@"X:\root\a\sub\f3", tree.GetPath(tree.Find(@"a\sub\f3")));
        Assert.Equal(157, tree.GetFreeable(ScanTree.Root));
    }

    [Fact]
    public void Freeable_IsCreditedToLowestCommonAncestor()
    {
        var tree = Build((b, root) =>
        {
            Add(b, root, ("x", NodeFlags.Directory, 0, 1, 1));
            Add(b, 1, ("y", NodeFlags.Directory, 0, 2, 1), ("z", NodeFlags.Directory, 0, 3, 1));
            Add(b, 2, ("h1", NodeFlags.None, 1000, 99, 2));
            Add(b, 3, ("h2", NodeFlags.None, 1000, 99, 2));
        });

        Assert.Equal(0, tree.GetFreeable(tree.Find(@"x\y")));
        Assert.Equal(0, tree.GetFreeable(tree.Find(@"x\z")));
        Assert.Equal(1000, tree.GetFreeable(tree.Find("x")));
        Assert.Equal(1000, tree.GetTotalSize(ScanTree.Root));
        Assert.Equal(2, tree.GetFileCount(ScanTree.Root));
    }

    [Fact]
    public void Proportional_DistributesRemainderToPrimary()
    {
        var tree = Build((b, root) =>
            Add(b, root, ("a", NodeFlags.None, 10, 7, 3), ("b", NodeFlags.None, 10, 7, 3), ("c", NodeFlags.None, 10, 7, 3)));

        tree.Aggregate(HardlinkMode.Proportional);
        Assert.Equal(4, tree.GetTotalSize(tree.Find("a")));
        Assert.Equal(3, tree.GetTotalSize(tree.Find("b")));
        Assert.Equal(10, tree.GetTotalSize(ScanTree.Root));
    }

    [Fact]
    public void NamePool_SpansMultipleChunks()
    {
        var tree = Build((b, root) =>
        {
            var entries = Enumerable.Range(0, 60_000).Select(i => ($"file-{i:D6}-padding-padding", NodeFlags.None, 1L, (ulong)i + 10, 1)).ToArray();
            Add(b, root, entries);
        });

        Assert.Equal(60_000, tree.GetFileCount(ScanTree.Root));
        Assert.True(tree.Find("file-059999-padding-padding") > 0);
        Assert.True(tree.Find("file-000000-padding-padding") > 0);
    }
}
