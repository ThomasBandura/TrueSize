using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using TrueSize.Core.Interop;
using TrueSize.Core.Model;
using TrueSize.Core.Scanning;

namespace TrueSize.Core.Remote;

/// <summary>
/// Server side of TrueSize.ScanHelper. Security model:
/// <list type="bullet">
/// <item>Random pipe name chosen by the client; created with FirstPipeInstance so a pre-existing (squatted) pipe fails.</item>
/// <item>Pipe DACL: read/write for the current user's SID only; network logons explicitly denied; one instance.</item>
/// <item>The connected client's process id must equal the id passed by the launching process.</item>
/// <item>Only ScanRequest (local fixed volumes), Cancel and Bye are accepted; anything else ends the session.</item>
/// <item>Exits after Bye, a disconnect, a protocol error or an idle timeout.</item>
/// </list>
/// </summary>
public static class HelperServer
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;
    public const int ExitPipe = 3;
    public const int ExitUnauthorizedClient = 4;
    public const int ExitTimeout = 5;
    public const int ExitProtocol = 6;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Arguments: --pipe &lt;TrueSize-guid&gt; --client-pid &lt;pid&gt; [--idle-seconds &lt;n&gt;].</summary>
    public static int Run(string[] args)
    {
        if (!TryParse(args, out string pipeName, out int clientPid, out var idle))
        {
            Console.Error.WriteLine("Usage: TrueSize.ScanHelper --pipe TrueSize-<32 hex> --client-pid <pid> [--idle-seconds <n>]");
            return ExitUsage;
        }

        NamedPipeServerStream pipe;
        try
        {
            pipe = CreatePipe(pipeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot create pipe: {ex.Message}");
            return ExitPipe;
        }

        using (pipe)
        {
            using (var connect = new CancellationTokenSource(ConnectTimeout))
            {
                try
                {
                    pipe.WaitForConnectionAsync(connect.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return ExitTimeout;
                }
            }

            if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actualPid) || actualPid != (uint)clientPid)
            {
                pipe.Disconnect();
                return ExitUnauthorizedClient;
            }

            return Serve(pipe, idle);
        }
    }

    internal static bool TryParse(string[] args, out string pipeName, out int clientPid, out TimeSpan idle)
    {
        pipeName = "";
        clientPid = 0;
        idle = DefaultIdleTimeout;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pipe":
                    pipeName = args[i + 1];
                    break;
                case "--client-pid" when int.TryParse(args[i + 1], out int pid) && pid > 0:
                    clientPid = pid;
                    break;
                case "--idle-seconds" when int.TryParse(args[i + 1], out int seconds) && seconds is > 0 and <= 86400:
                    idle = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    return false;
            }
        }

        return args.Length % 2 == 0 && HelperProtocol.IsValidPipeName(pipeName) && clientPid > 0;
    }

    private static NamedPipeServerStream CreatePipe(string name)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("No user SID.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 64 * 1024,
            outBufferSize: 1024 * 1024,
            security);
    }

    private static int Serve(NamedPipeServerStream pipe, TimeSpan idle)
    {
        HelperProtocol.Write(pipe, MessageType.Hello, w =>
            HelperProtocol.WriteHello(w, new HelperHello(HelperProtocol.Version, ScannerSelector.IsElevated(), Environment.ProcessId)));

        // Exactly one outstanding read at any time; it doubles as the cancel listener while a scan runs.
        Task<(MessageType Type, byte[] Payload)?> pendingRead = ReadAsync(pipe);
        while (true)
        {
            // WaitAny never throws (Task.Wait would rethrow a faulted read).
            if (Task.WaitAny([pendingRead], idle) < 0)
            {
                return ExitTimeout;
            }

            (MessageType Type, byte[] Payload)? message;
            try
            {
                message = pendingRead.Result;
            }
            catch (AggregateException)
            {
                return ExitProtocol;
            }

            if (message is null || message.Value.Type == MessageType.Bye)
            {
                return ExitOk;
            }

            switch (message.Value.Type)
            {
                case MessageType.ScanRequest:
                    pendingRead = HandleScan(pipe, message.Value.Payload);
                    break;
                case MessageType.Cancel:
                    // Late cancel after a scan finished: nothing to do.
                    pendingRead = ReadAsync(pipe);
                    break;
                default:
                    return ExitProtocol;
            }

            if (pendingRead.IsCompleted && pendingRead.Status != TaskStatus.RanToCompletion)
            {
                return ExitProtocol;
            }
        }
    }

    private static Task<(MessageType Type, byte[] Payload)?> ReadAsync(Stream pipe) => Task.Run(() => HelperProtocol.Read(pipe));

    /// <summary>Runs one scan; returns the read task that was listening during the scan (it may hold the next request).</summary>
    private static Task<(MessageType Type, byte[] Payload)?> HandleScan(NamedPipeServerStream pipe, byte[] payload)
    {
        ScanRequestMessage request;
        string path;
        try
        {
            request = HelperProtocol.ReadScanRequest(payload);
            path = ValidatePath(request.Path);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or EndOfStreamException or IOException or NotSupportedException)
        {
            HelperProtocol.Write(pipe, MessageType.Error, w => HelperProtocol.WriteError(w, HelperErrorKind.Rejected, ex.Message));
            return ReadAsync(pipe);
        }

        using var cancellation = new CancellationTokenSource();
        var progress = new ScanProgress();
        var scan = Task.Run(() => RunBestScanner(path, request.Options, progress, cancellation.Token));
        var listener = ReadAsync(pipe);

        // WaitAny instead of Wait: a cancelled or faulted scan must not throw here, its outcome is reported below.
        while (Task.WaitAny([scan], ProgressInterval) < 0)
        {
            if (listener.IsCompleted)
            {
                var next = listener.Status == TaskStatus.RanToCompletion ? listener.Result : null;
                if (next is null || next.Value.Type is MessageType.Cancel or MessageType.Bye)
                {
                    cancellation.Cancel();
                    if (next?.Type == MessageType.Cancel)
                    {
                        listener = ReadAsync(pipe);
                    }
                }
            }

            HelperProtocol.Write(pipe, MessageType.Progress, w =>
                HelperProtocol.WriteProgress(w, new HelperProgress(progress.Files, progress.Directories, progress.AllocatedBytes, progress.Errors)));
        }

        try
        {
            var result = scan.GetAwaiter().GetResult();
            HelperProtocol.Write(pipe, MessageType.Result, ReadOnlySpan<byte>.Empty);
            ScanTreeSerializer.Write(pipe, result);
            pipe.Flush();
        }
        catch (OperationCanceledException)
        {
            HelperProtocol.Write(pipe, MessageType.Error, w => HelperProtocol.WriteError(w, HelperErrorKind.Cancelled, "Scan cancelled."));
        }
        catch (Exception ex)
        {
            HelperProtocol.Write(pipe, MessageType.Error, w => HelperProtocol.WriteError(w, HelperErrorKind.Failed, $"{ex.GetType().Name}: {ex.Message}"));
        }

        return listener;
    }

    /// <summary>Only fully qualified directories on local fixed drives (no UNC, device or relative paths).</summary>
    internal static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000 || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"Rejected path '{path}': only fully qualified local paths are allowed.");
        }

        string full = Path.GetFullPath(path);
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType != DriveType.Fixed)
        {
            throw new ArgumentException($"Rejected path '{path}': drive type {drive.DriveType} is not a local fixed drive.");
        }

        if (!Directory.Exists(full))
        {
            throw new ArgumentException($"Rejected path '{path}': directory not found.");
        }

        return full;
    }

    /// <summary>Same choice as ScannerSelector; falls back to the directory scanner if the MFT scanner fails.</summary>
    private static ScanResult RunBestScanner(string path, ScanOptions options, ScanProgress progress, CancellationToken cancellationToken)
    {
        var choice = ScannerSelector.Select(path);
        if (choice.IsFastPath)
        {
            try
            {
                return choice.Scanner.Scan(path, options, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Fall through to the directory scanner.
            }
        }

        return new DirectoryScanner().Scan(path, options, progress, cancellationToken);
    }
}
