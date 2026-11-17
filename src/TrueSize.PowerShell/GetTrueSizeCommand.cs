using System.Management.Automation;
using TrueSize.Core.Model;
using TrueSize.Core.Scanning;

namespace TrueSize.PowerShell;

/// <summary>
/// <para type="synopsis">Measures the real disk usage of a directory tree.</para>
/// <para type="description">Counts hard links once, does not follow junctions or symbolic links, and reports
/// logical size, allocated size and freeable size separately. Output is aggregated: by default only the direct
/// children of the path are emitted.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TrueSize", DefaultParameterSetName = Items)]
[OutputType(typeof(TrueSizeItem), ParameterSetName = [Items])]
[OutputType(typeof(ScanResult), ParameterSetName = [RawSet])]
public sealed class GetTrueSizeCommand : PSCmdlet
{
    private const string Items = "Items";
    private const string RawSet = "Raw";
    private const int MaxVerboseErrors = 100;

    private CancellationTokenSource? _cancellation;

    /// <summary>Directory to scan. Defaults to the current location.</summary>
    [Parameter(Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("FullName", "PSPath")]
    public string? Path { get; set; }

    /// <summary>Maximum depth below the path to emit (0 = the path itself only).</summary>
    [Parameter(ParameterSetName = Items)]
    [ValidateRange(0, int.MaxValue)]
    public int Depth { get; set; } = 1;

    /// <summary>Emit only the n largest items (flat: overall; -AsTree: per directory). 0 = all.</summary>
    [Parameter(ParameterSetName = Items)]
    [ValidateRange(0, int.MaxValue)]
    public int Top { get; set; }

    /// <summary>Skip items whose allocated size is below this many bytes. Accepts 10MB etc.</summary>
    [Parameter(ParameterSetName = Items)]
    [ValidateRange(0, long.MaxValue)]
    public long MinSize { get; set; }

    /// <summary>How hard-linked files are attributed.</summary>
    [Parameter]
    public HardlinkMode HardlinkMode { get; set; } = HardlinkMode.FirstFound;

    /// <summary>Emit in tree order (children sorted by size) with indented names instead of a flat sorted list.</summary>
    [Parameter(ParameterSetName = Items)]
    public SwitchParameter AsTree { get; set; }

    /// <summary>Include alternate data streams (opens every file; slow).</summary>
    [Parameter]
    public SwitchParameter IncludeAlternateStreams { get; set; }

    /// <summary>Skip per-file link count queries (about 3-7x faster). Freeable then ignores links outside the path.</summary>
    [Parameter]
    public SwitchParameter SkipLinkCounts { get; set; }

    /// <summary>Maximum number of directories scanned in parallel. 0 = number of processors.</summary>
    [Parameter]
    [ValidateRange(0, 1024)]
    public int ThrottleLimit { get; set; }

    /// <summary>Return the complete scan result (one object) instead of items.</summary>
    [Parameter(Mandatory = true, ParameterSetName = RawSet)]
    public SwitchParameter Raw { get; set; }

    protected override void ProcessRecord()
    {
        string root = ResolvePath(Path ?? SessionState.Path.CurrentFileSystemLocation.ProviderPath);
        var choice = ScannerSelector.Select(root);
        if (!choice.IsElevated)
        {
            WriteWarning(choice.Reason);
        }
        else
        {
            WriteVerbose(choice.Reason);
        }

        var options = new ScanOptions
        {
            HardlinkMode = HardlinkMode,
            IncludeAlternateStreams = IncludeAlternateStreams,
            ResolveLinkCounts = !SkipLinkCounts,
            MaxParallelism = ThrottleLimit,
        };

        var result = RunScan(choice.Scanner, root, options);
        if (result is null)
        {
            return;
        }

        ReportErrors(result);
        WriteVerbose($"{choice.Scanner.Name} scanner, {choice.Volume.FileSystem}: {result.Tree.GetFileCount(ScanTree.Root):N0} files, " +
                     $"{result.Tree.GetDirectoryCount(ScanTree.Root):N0} directories in {result.Duration.TotalSeconds:F2} s.");

        if (Raw)
        {
            WriteObject(result);
            return;
        }

        var tree = result.Tree;
        if (AsTree)
        {
            EmitTree(tree, ScanTree.Root, 0);
        }
        else
        {
            EmitFlat(tree);
        }
    }

    protected override void StopProcessing() => _cancellation?.Cancel();

    private string ResolvePath(string path)
    {
        var resolved = SessionState.Path.GetResolvedProviderPathFromPSPath(path, out var provider);
        if (provider.Name != "FileSystem")
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException($"'{path}' is not a file system path."), "NotFileSystemPath", ErrorCategory.InvalidArgument, path));
        }

        if (resolved.Count != 1)
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException($"'{path}' resolves to {resolved.Count} paths; specify exactly one directory."), "AmbiguousPath", ErrorCategory.InvalidArgument, path));
        }

        if (!Directory.Exists(resolved[0]))
        {
            ThrowTerminatingError(new ErrorRecord(
                new DirectoryNotFoundException($"'{resolved[0]}' is not a directory."), "NotADirectory", ErrorCategory.InvalidArgument, path));
        }

        return resolved[0];
    }

    private ScanResult? RunScan(IVolumeScanner scanner, string root, ScanOptions options)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var progress = new ScanProgress();
        var record = new ProgressRecord(1, $"TrueSize: {root}", "Scanning");

        // Scan on a worker thread; WriteProgress must be called from the pipeline thread.
        var task = Task.Run(() => scanner.Scan(root, options, progress, cancellation.Token));
        try
        {
            while (!task.Wait(250))
            {
                record.StatusDescription =
                    $"{progress.Files:N0} files, {progress.Directories:N0} directories, {SizeFormatter.Format(progress.AllocatedBytes)}";
                WriteProgress(record);
            }

            return task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (!task.IsCompleted)
            {
                cancellation.Cancel();
                task.ContinueWith(static _ => { }, TaskScheduler.Default).Wait();
            }

            _cancellation = null;
            record.RecordType = ProgressRecordType.Completed;
            try
            {
                WriteProgress(record);
            }
            catch (PipelineStoppedException)
            {
            }
        }
    }

    private void ReportErrors(ScanResult result)
    {
        if (result.Errors.Count == 0)
        {
            return;
        }

        foreach (var error in result.Errors.Take(MaxVerboseErrors))
        {
            WriteVerbose($"{error.Operation} '{error.Path}': {error.Message} ({error.Win32Error})");
        }

        WriteWarning($"{result.Errors.Count:N0} item(s) could not be read and are counted with partial or zero size. Use -Verbose for details.");
    }

    private void EmitFlat(ScanTree tree)
    {
        if (Depth == 0)
        {
            WriteObject(CreateItem(tree, ScanTree.Root, 0, flat: true));
            return;
        }

        // Collect within depth; MinSize prunes whole subtrees because a child never exceeds its parent.
        var candidates = new List<(int Node, int Depth)>();
        var stack = new Stack<(int Node, int Depth)>();
        stack.Push((ScanTree.Root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            foreach (int child in tree.Children(node))
            {
                if (tree.GetTotalAllocated(child) < MinSize)
                {
                    continue;
                }

                candidates.Add((child, depth + 1));
                if (depth + 1 < Depth)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }

        candidates.Sort((a, b) => CompareBySize(tree, a.Node, b.Node));
        int count = Top > 0 ? Math.Min(Top, candidates.Count) : candidates.Count;
        for (int i = 0; i < count; i++)
        {
            WriteObject(CreateItem(tree, candidates[i].Node, candidates[i].Depth, flat: true));
        }
    }

    private void EmitTree(ScanTree tree, int node, int depth)
    {
        WriteObject(CreateItem(tree, node, depth, flat: false));
        if (depth >= Depth)
        {
            return;
        }

        var children = new List<int>();
        foreach (int child in tree.Children(node))
        {
            if (tree.GetTotalAllocated(child) >= MinSize)
            {
                children.Add(child);
            }
        }

        children.Sort((a, b) => CompareBySize(tree, a, b));
        int count = Top > 0 ? Math.Min(Top, children.Count) : children.Count;
        for (int i = 0; i < count; i++)
        {
            EmitTree(tree, children[i], depth + 1);
        }
    }

    // Largest allocation first; ties ordered by name so the output is deterministic.
    private static int CompareBySize(ScanTree tree, int a, int b)
    {
        int c = tree.GetTotalAllocated(b).CompareTo(tree.GetTotalAllocated(a));
        return c != 0 ? c : MemoryExtensions.CompareTo(tree.GetName(a), tree.GetName(b), StringComparison.OrdinalIgnoreCase);
    }

    private static TrueSizeItem CreateItem(ScanTree tree, int node, int depth, bool flat)
    {
        string fullName = tree.GetPath(node);
        string name = node == ScanTree.Root ? fullName : tree.GetName(node).ToString();
        long rootAllocated = tree.GetTotalAllocated(ScanTree.Root);
        string display = flat
            ? (node == ScanTree.Root ? fullName : System.IO.Path.GetRelativePath(tree.Info.RootPath, fullName))
            : new string(' ', depth * 2) + name;

        return new TrueSizeItem
        {
            Name = name,
            DisplayName = display,
            FullName = fullName,
            Depth = depth,
            IsDirectory = (tree.GetFlags(node) & NodeFlags.Directory) != 0,
            Size = tree.GetTotalSize(node),
            Allocated = tree.GetTotalAllocated(node),
            Freeable = tree.GetFreeable(node),
            HardlinkAllocated = tree.GetHardlinkAllocated(node),
            Percent = rootAllocated > 0 ? 100.0 * tree.GetTotalAllocated(node) / rootAllocated : 0,
            Files = tree.GetFileCount(node),
            Directories = tree.GetDirectoryCount(node),
            LinkCount = tree.GetLinkCount(node),
            Flags = tree.GetFlags(node),
        };
    }
}
