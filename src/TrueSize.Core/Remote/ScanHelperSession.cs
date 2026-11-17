using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using TrueSize.Core.Interop;
using TrueSize.Core.Model;
using TrueSize.Core.Scanning;

namespace TrueSize.Core.Remote;

/// <summary>Raised when the helper refuses or fails a request.</summary>
public sealed class ScanHelperException(string message) : Exception(message);

/// <summary>
/// Client side of the scan helper. One session = one helper process (one UAC prompt) that can serve several scans.
/// The client verifies that the pipe server is the process it started.
/// </summary>
public sealed class ScanHelperSession : IDisposable
{
    private const string HelperFileName = "TrueSize.ScanHelper.exe";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private readonly Lock _writeLock = new();
    private bool _disposed;

    private ScanHelperSession(Process process, NamedPipeClientStream pipe, HelperHello hello)
    {
        _process = process;
        _pipe = pipe;
        IsElevated = hello.IsElevated;
    }

    /// <summary>True if the helper process runs elevated.</summary>
    public bool IsElevated { get; }

    /// <summary>Timings of the last scan: until the result header arrived, and receiving + rebuilding the tree.</summary>
    public (TimeSpan UntilResult, TimeSpan Transfer) LastTimings { get; private set; }

    /// <summary>Default location: next to the calling assembly (app folder or PowerShell module folder).</summary>
    public static string DefaultHelperPath => Path.Combine(AppContext.BaseDirectory, HelperFileName);

    /// <summary>
    /// Starts the helper. With <paramref name="elevate"/> and a non-elevated caller, Windows shows a UAC prompt ("runas");
    /// an already elevated caller starts it directly (it inherits the elevation).
    /// </summary>
    /// <exception cref="OperationCanceledException">The user declined the UAC prompt.</exception>
    public static ScanHelperSession Start(string? helperPath = null, bool elevate = true)
    {
        helperPath ??= DefaultHelperPath;
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("Scan helper not found.", helperPath);
        }

        string pipeName = HelperProtocol.NewPipeName();
        bool runas = elevate && !ScannerSelector.IsElevated();
        var startInfo = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = runas,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (runas)
        {
            startInfo.Verb = "runas";
            startInfo.Arguments = $"--pipe {pipeName} --client-pid {Environment.ProcessId}";
        }
        else
        {
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--client-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The scan helper did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Native.ERROR_CANCELLED)
        {
            throw new OperationCanceledException("Elevation was declined.", ex);
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(ConnectTimeout);

            // Only talk to the process we started (protects against a squatted pipe name).
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != (uint)process.Id)
            {
                throw new UnauthorizedAccessException("The pipe server is not the started scan helper.");
            }

            var message = HelperProtocol.Read(pipe) ?? throw new EndOfStreamException("The scan helper closed the connection.");
            if (message.Type != MessageType.Hello)
            {
                throw new InvalidDataException($"Unexpected first message {message.Type}.");
            }

            var hello = HelperProtocol.ReadHello(message.Payload);
            if (hello.ProtocolVersion != HelperProtocol.Version || hello.ProcessId != process.Id)
            {
                throw new InvalidDataException($"Incompatible scan helper (protocol {hello.ProtocolVersion}, pid {hello.ProcessId}).");
            }

            return new ScanHelperSession(process, pipe, hello);
        }
        catch
        {
            pipe.Dispose();
            if (!process.HasExited)
            {
                process.Kill();
            }

            process.Dispose();
            throw;
        }
    }

    public ScanResult Scan(string path, ScanOptions options, ScanProgress? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Send(MessageType.ScanRequest, w => HelperProtocol.WriteScanRequest(w, new ScanRequestMessage(Path.GetFullPath(path), options)));

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                Send(MessageType.Cancel, static _ => { });
            }
            catch (IOException)
            {
            }
        });

        var sw = Stopwatch.StartNew();
        while (true)
        {
            var message = HelperProtocol.Read(_pipe) ?? throw new ScanHelperException("The scan helper closed the connection.");
            switch (message.Type)
            {
                case MessageType.Progress:
                    var p = HelperProtocol.ReadProgress(message.Payload);
                    progress?.Set(p.Files, p.Directories, p.Bytes, p.Errors);
                    break;
                case MessageType.Result:
                    var untilResult = sw.Elapsed;
                    var result = ScanTreeSerializer.Read(_pipe);
                    LastTimings = (untilResult, sw.Elapsed - untilResult);
                    return result;
                case MessageType.Error:
                    var (kind, text) = HelperProtocol.ReadError(message.Payload);
                    if (kind == HelperErrorKind.Cancelled)
                    {
                        throw new OperationCanceledException(text, cancellationToken);
                    }

                    throw new ScanHelperException(text);
                default:
                    throw new InvalidDataException($"Unexpected message {message.Type}.");
            }
        }
    }

    private void Send(MessageType type, Action<BinaryWriter> body)
    {
        lock (_writeLock)
        {
            HelperProtocol.Write(_pipe, type, body);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Send(MessageType.Bye, static _ => { });
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _pipe.Dispose();
        if (!_process.WaitForExit(5000))
        {
            _process.Kill();
        }

        _process.Dispose();
    }
}

/// <summary>
/// IVolumeScanner over a helper session. Owns the session if created with <see cref="StartNew"/>.
/// </summary>
public sealed class HelperScanner(ScanHelperSession session, bool ownsSession = false) : IVolumeScanner, IDisposable
{
    public string Name => "Helper";

    public static HelperScanner StartNew(string? helperPath = null, bool elevate = true) => new(ScanHelperSession.Start(helperPath, elevate), ownsSession: true);

    public ScanResult Scan(string path, ScanOptions options, ScanProgress? progress = null, CancellationToken cancellationToken = default) =>
        session.Scan(path, options, progress, cancellationToken);

    public void Dispose()
    {
        if (ownsSession)
        {
            session.Dispose();
        }
    }
}
