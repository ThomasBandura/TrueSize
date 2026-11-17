using System.Diagnostics;
using System.IO.Pipes;
using TrueSize.Core.Model;
using TrueSize.Core.Remote;
using TrueSize.Core.Scanning;
using TrueSize.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace TrueSize.Core.Tests;

[Collection(TestTreeCollection.Name)]
public sealed class ScanHelperTests(TestTrees trees, ITestOutputHelper output)
{
    private static readonly ScanOptions WithStreams = new() { IncludeAlternateStreams = true };

    /// <summary>The helper's own build output (framework-dependent exe next to TrueSize.Core.dll).</summary>
    private static string HelperPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TrueSize.sln")))
            {
                dir = dir.Parent;
            }

            string configuration = AppContext.BaseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
            return Path.Combine(dir!.FullName, "src", "TrueSize.ScanHelper", "bin", configuration, "net10.0-windows", "TrueSize.ScanHelper.exe");
        }
    }

    private TestTree Fixture => trees.Get("NTFS");

    // ---------- Serialization ----------

    [Fact]
    public void Serializer_RoundTrip_PreservesTreeErrorsAndInfo()
    {
        var original = new DirectoryScanner().Scan(Fixture.Root, WithStreams);
        using var buffer = new MemoryStream();
        ScanTreeSerializer.Write(buffer, original);
        buffer.Position = 0;
        var copy = ScanTreeSerializer.Read(buffer);

        Assert.Equal(buffer.Length, buffer.Position);
        Assert.Empty(ScannerTests.CompareTrees(copy.Tree, original.Tree));
        Assert.Equal(original.Tree.Info, copy.Tree.Info);
        Assert.Equal(original.Errors, copy.Errors);
        Assert.Equal(original.Duration, copy.Duration);
        for (int i = 0; i < original.Tree.Count; i++)
        {
            Assert.Equal(original.Tree.TryGetReparseTag(i, out uint a) ? a : 0, copy.Tree.TryGetReparseTag(i, out uint b) ? b : 0);
        }
    }

    /// <summary>Every Write on an overlapped pipe is a full I/O operation: the cost must not grow per node or per tag.</summary>
    [Fact]
    public void Serializer_UsesBulkWrites_IndependentOfTagCount()
    {
        const int n = 200_000;
        var info = new ScanTreeInfo(@"X:\", @"X:\", 1, "NTFS", "test", true, false);
        var parent = new int[n];
        var firstChild = new int[n];
        var nextSibling = new int[n];
        parent[0] = -1;
        firstChild[0] = n > 1 ? n - 1 : -1;
        nextSibling[0] = -1;
        var tags = new Dictionary<int, uint>();
        for (int i = 1; i < n; i++)
        {
            parent[i] = 0;
            firstChild[i] = -1;
            nextSibling[i] = i - 1 >= 1 ? i - 1 : -1;
            tags[i] = 0x80000018;
        }

        var tree = new ScanTree(info, n, parent, firstChild, nextSibling, new int[n], new ushort[n], new NodeFlags[n],
            new long[n], new long[n], new FileId128[n], new int[n], [], tags);
        var errors = Enumerable.Range(0, 1000).Select(i => new ScanError($@"X:\e{i}", 5, "OpenDirectory")).ToList();

        using var counting = new CountingStream();
        ScanTreeSerializer.Write(counting, new ScanResult(tree, errors, TimeSpan.Zero));
        output.WriteLine($"{counting.Writes} writes, {counting.Length:N0} bytes");
        Assert.InRange(counting.Writes, 1, 200);

        counting.Position = 0;
        var copy = ScanTreeSerializer.Read(counting);
        Assert.Equal(n - 1, copy.Tree.RawReparseTags.Count);
        Assert.Equal(errors, copy.Errors);
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            base.Write(buffer, offset, count);
        }

        public override void WriteByte(byte value)
        {
            Writes++;
            base.WriteByte(value);
        }
    }

    [Fact]
    public void Serializer_RejectsChildBeforeParent()
    {
        // Node 1 claims node 2 as parent: aggregation would be wrong, the reader must refuse it.
        var info = new ScanTreeInfo(@"X:\", @"X:\", 1, "NTFS", "test", true, false);
        var tree = new ScanTree(info, 3, [-1, 2, 0], [2, -1, 1], [-1, -1, -1], [0, 0, 0], [1, 1, 1],
            [NodeFlags.Directory, NodeFlags.None, NodeFlags.Directory], new long[3], new long[3], new FileId128[3], new int[3], ['a'], []);
        using var buffer = new MemoryStream();
        ScanTreeSerializer.Write(buffer, new ScanResult(tree, [], TimeSpan.Zero));
        buffer.Position = 0;
        Assert.Throws<InvalidDataException>(() => ScanTreeSerializer.Read(buffer));
    }

    [Fact]
    public void Protocol_RejectsOversizedAndUnknownMessages()
    {
        Assert.Throws<InvalidDataException>(() => HelperProtocol.Read(new MemoryStream([(byte)MessageType.ScanRequest, 0xFF, 0xFF, 0xFF, 0x7F])));
        Assert.Throws<InvalidDataException>(() => HelperProtocol.Read(new MemoryStream([0xEE, 0, 0, 0, 0])));
        Assert.Null(HelperProtocol.Read(new MemoryStream()));
    }

    [Fact]
    public void Protocol_ScanRequest_RoundTrip()
    {
        var options = new ScanOptions { HardlinkMode = HardlinkMode.Separate, IncludeAlternateStreams = true, ResolveLinkCounts = false, ResolveAllReparseTags = true, MaxParallelism = 3 };
        using var buffer = new MemoryStream();
        HelperProtocol.Write(buffer, MessageType.ScanRequest, w => HelperProtocol.WriteScanRequest(w, new ScanRequestMessage(@"C:\x", options)));
        buffer.Position = 0;
        var message = HelperProtocol.Read(buffer)!.Value;
        var request = HelperProtocol.ReadScanRequest(message.Payload);
        Assert.Equal(@"C:\x", request.Path);
        Assert.Equal(options, request.Options);
    }

    [Theory]
    [InlineData(new[] { "--pipe", "TrueSize-0123456789abcdef0123456789abcdef", "--client-pid", "42" }, true)]
    [InlineData(new[] { "--pipe", "OtherPipe", "--client-pid", "42" }, false)]
    [InlineData(new[] { "--pipe", "TrueSize-0123456789abcdef0123456789abcdef" }, false)]
    [InlineData(new[] { "--pipe", "TrueSize-0123456789abcdef0123456789abcdef", "--client-pid", "0" }, false)]
    [InlineData(new[] { "--pipe", "TrueSize-0123456789abcdef0123456789abcdef", "--client-pid", "42", "--exec", "x" }, false)]
    public void Arguments_AreValidatedStrictly(string[] args, bool valid) =>
        Assert.Equal(valid, HelperServer.TryParse(args, out _, out _, out _));

    [Theory]
    [InlineData(@"\\server\share\dir")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\C:")]
    [InlineData(@"relative\path")]
    [InlineData(@"C:\this\does\not\exist\at\all")]
    [InlineData("")]
    public void ValidatePath_RejectsNonLocalOrInvalidPaths(string path) =>
        Assert.Throws<ArgumentException>(() => HelperServer.ValidatePath(path));

    [Fact]
    public void ValidatePath_AcceptsLocalDirectory() =>
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), HelperServer.ValidatePath(Path.GetTempPath()));

    // ---------- End to end (helper process, not elevated unless the test runs elevated) ----------

    [Fact]
    public void Session_ScanMatchesDirectScan_AndCanBeReused()
    {
        using var session = ScanHelperSession.Start(HelperPath, elevate: false);

        // The helper picks the same scanner as ScannerSelector (MFT when elevated on NTFS); compare like with like.
        IVolumeScanner expectedScanner = session.IsElevated ? new NtfsMftScanner() : new DirectoryScanner();
        var direct = expectedScanner.Scan(Fixture.Root, WithStreams);

        for (int round = 0; round < 2; round++)
        {
            var progress = new ScanProgress();
            var remote = session.Scan(Fixture.Root, WithStreams, progress);
            Assert.Empty(ScannerTests.CompareTrees(remote.Tree, direct.Tree));
        }
    }

    [Fact]
    public void Session_RejectedPath_ReportsError_AndStaysUsable()
    {
        using var session = ScanHelperSession.Start(HelperPath, elevate: false);
        var ex = Assert.Throws<ScanHelperException>(() => session.Scan(@"\\server\share", new ScanOptions()));
        Assert.Contains("Rejected", ex.Message);
        Assert.True(session.Scan(Fixture.Root, new ScanOptions()).Tree.Count > 1);
    }

    [Fact]
    public void Session_Cancel_StopsScan_AndStaysUsable()
    {
        using var session = ScanHelperSession.Start(HelperPath, elevate: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            session.Scan(Environment.GetFolderPath(Environment.SpecialFolder.Windows), new ScanOptions(), null, cts.Token));
        output.WriteLine($"cancelled after {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(session.Scan(Fixture.Root, new ScanOptions()).Tree.Count > 1);
    }

    [Fact]
    public void Helper_RejectsClientWithWrongProcessId()
    {
        string name = HelperProtocol.NewPipeName();
        using var helper = StartRaw(name, clientPid: 4); // PID of the System process, never this test
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        client.Connect(10_000);
        Assert.True(helper.WaitForExit(10_000));
        Assert.Equal(HelperServer.ExitUnauthorizedClient, helper.ExitCode);
        Assert.Null(SafeRead(client));
    }

    [Fact]
    public void Helper_RefusesSquattedPipeName()
    {
        string name = HelperProtocol.NewPipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 1);
        using var helper = StartRaw(name, Environment.ProcessId);
        Assert.True(helper.WaitForExit(10_000));
        Assert.Equal(HelperServer.ExitPipe, helper.ExitCode);
    }

    [Fact]
    public void Helper_ExitsAfterIdleTimeout()
    {
        string name = HelperProtocol.NewPipeName();
        using var helper = StartRaw(name, Environment.ProcessId, "--idle-seconds", "1");
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        client.Connect(10_000);
        Assert.Equal(MessageType.Hello, HelperProtocol.Read(client)!.Value.Type);
        Assert.True(helper.WaitForExit(10_000));
        Assert.Equal(HelperServer.ExitTimeout, helper.ExitCode);
    }

    [Fact]
    public void Helper_EndsSessionOnUnexpectedMessage()
    {
        string name = HelperProtocol.NewPipeName();
        using var helper = StartRaw(name, Environment.ProcessId);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        client.Connect(10_000);
        HelperProtocol.Read(client);
        HelperProtocol.Write(client, MessageType.Result, ReadOnlySpan<byte>.Empty); // server-only message
        Assert.True(helper.WaitForExit(10_000));
        Assert.Equal(HelperServer.ExitProtocol, helper.ExitCode);
    }

    [ElevatedFact]
    public void Session_Elevated_UsesMftScanner_AndMatchesDirectMftScan()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        using var session = ScanHelperSession.Start(HelperPath, elevate: false); // child inherits the elevation
        Assert.True(session.IsElevated);
        var sw = Stopwatch.StartNew();
        var remote = session.Scan(path, new ScanOptions());
        output.WriteLine($"helper scan incl. transfer: {sw.ElapsedMilliseconds} ms, {remote.Tree.Count:N0} nodes, scanner {remote.Tree.Info.Scanner}");
        var direct = new NtfsMftScanner().Scan(path, new ScanOptions());

        Assert.Equal("NtfsMft", remote.Tree.Info.Scanner);
        Assert.Empty(ScannerTests.CompareTrees(remote.Tree, direct.Tree));
    }

    private static Process StartRaw(string pipeName, int clientPid, params string[] extra)
    {
        var psi = new ProcessStartInfo(HelperPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--client-pid");
        psi.ArgumentList.Add(clientPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string arg in extra)
        {
            psi.ArgumentList.Add(arg);
        }

        return Process.Start(psi)!;
    }

    private static (MessageType, byte[])? SafeRead(Stream stream)
    {
        try
        {
            return HelperProtocol.Read(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
