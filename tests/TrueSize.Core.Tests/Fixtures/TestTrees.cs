using TrueSize.Core.Scanning;

namespace TrueSize.Core.Tests.Fixtures;

/// <summary>One fixture tree per available file system: NTFS under %TEMP%, ReFS next to the test binaries (if on ReFS).</summary>
public sealed class TestTrees : IDisposable
{
    private readonly Dictionary<string, TestTree> _trees = [];

    public TestTrees()
    {
        TryAdd(Path.GetTempPath());
        TryAdd(Path.Combine(AppContext.BaseDirectory, "fixtures"));
    }

    private void TryAdd(string baseDirectory)
    {
        Directory.CreateDirectory(baseDirectory);
        string fs = VolumeInfo.FromPath(baseDirectory).FileSystem;
        if (!_trees.ContainsKey(fs))
        {
            _trees[fs] = TestTree.Create(baseDirectory);
        }
    }

    public TestTree Get(string fileSystem) =>
        _trees.TryGetValue(fileSystem, out var tree) ? tree : throw SkipException(fileSystem);

    public bool Has(string fileSystem) => _trees.ContainsKey(fileSystem);

    private static Exception SkipException(string fs) => new InvalidOperationException($"No {fs} volume available for fixtures.");

    public void Dispose()
    {
        foreach (var tree in _trees.Values)
        {
            tree.Dispose();
        }
    }

    public static TheoryData<string> FileSystems => new() { "NTFS", "ReFS" };
}

[CollectionDefinition(Name)]
public sealed class TestTreeCollection : ICollectionFixture<TestTrees>
{
    public const string Name = "TestTrees";
}
