using System.Buffers.Binary;
using System.Text.RegularExpressions;
using TrueSize.Core.Model;

namespace TrueSize.Core.Remote;

/// <summary>Messages between the app (client) and TrueSize.ScanHelper (server). Little-endian throughout.</summary>
internal enum MessageType : byte
{
    // Client -> helper
    ScanRequest = 1,
    Cancel = 2,
    Bye = 3,

    // Helper -> client
    Hello = 10,
    Progress = 11,
    Result = 12,
    Error = 13,
}

internal enum HelperErrorKind : byte
{
    Failed = 1,
    Cancelled = 2,
    Rejected = 3,
}

internal sealed record ScanRequestMessage(string Path, ScanOptions Options);

internal sealed record HelperHello(int ProtocolVersion, bool IsElevated, int ProcessId);

internal sealed record HelperProgress(long Files, long Directories, long Bytes, long Errors);

/// <summary>
/// Framing: [byte type][int32 length][payload]. Every message except Result carries a bounded payload; Result has
/// length 0 and is followed by a self-delimiting ScanTreeSerializer stream. The helper accepts only ScanRequest,
/// Cancel and Bye; anything else ends the session.
/// </summary>
internal static partial class HelperProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 64 * 1024;
    private const string PipePrefix = "TrueSize-";

    public static string NewPipeName() => PipePrefix + Guid.NewGuid().ToString("N");

    public static bool IsValidPipeName(string name) => PipeNameRegex().IsMatch(name);

    [GeneratedRegex("^TrueSize-[0-9a-f]{32}$")]
    private static partial Regex PipeNameRegex();

    public static void Write(Stream stream, MessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Message too large: {payload.Length} bytes.");
        }

        Span<byte> header = stackalloc byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    public static void Write(Stream stream, MessageType type, Action<BinaryWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            body(writer);
        }

        Write(stream, type, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>Reads one message header and payload. Returns null at end of stream.</summary>
    public static (MessageType Type, byte[] Payload)? Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[5];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length)
        {
            throw new EndOfStreamException("Truncated message header.");
        }

        var type = (MessageType)header[0];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header[1..]);
        if (length < 0 || length > MaxMessageBytes || !Enum.IsDefined(type))
        {
            throw new InvalidDataException($"Invalid message: type {header[0]}, length {length}.");
        }

        var payload = new byte[length];
        stream.ReadExactly(payload);
        return (type, payload);
    }

    public static void WriteScanRequest(BinaryWriter w, ScanRequestMessage request)
    {
        w.Write(request.Path);
        w.Write((byte)request.Options.HardlinkMode);
        w.Write(request.Options.ResolveLinkCounts);
        w.Write(request.Options.IncludeAlternateStreams);
        w.Write(request.Options.EnumerateCloudDirectories);
        w.Write(request.Options.ResolveAllReparseTags);
        w.Write(request.Options.MaxParallelism);
    }

    public static ScanRequestMessage ReadScanRequest(byte[] payload)
    {
        using var r = Reader(payload);
        string path = r.ReadString();
        var mode = (HardlinkMode)r.ReadByte();
        if (!Enum.IsDefined(mode))
        {
            throw new InvalidDataException($"Invalid hard link mode {(int)mode}.");
        }

        var options = new ScanOptions
        {
            HardlinkMode = mode,
            ResolveLinkCounts = r.ReadBoolean(),
            IncludeAlternateStreams = r.ReadBoolean(),
            EnumerateCloudDirectories = r.ReadBoolean(),
            ResolveAllReparseTags = r.ReadBoolean(),
            MaxParallelism = Math.Clamp(r.ReadInt32(), 0, 1024),
        };
        return new ScanRequestMessage(path, options);
    }

    public static void WriteHello(BinaryWriter w, HelperHello hello)
    {
        w.Write(hello.ProtocolVersion);
        w.Write(hello.IsElevated);
        w.Write(hello.ProcessId);
    }

    public static HelperHello ReadHello(byte[] payload)
    {
        using var r = Reader(payload);
        return new HelperHello(r.ReadInt32(), r.ReadBoolean(), r.ReadInt32());
    }

    public static void WriteProgress(BinaryWriter w, HelperProgress p)
    {
        w.Write(p.Files);
        w.Write(p.Directories);
        w.Write(p.Bytes);
        w.Write(p.Errors);
    }

    public static HelperProgress ReadProgress(byte[] payload)
    {
        using var r = Reader(payload);
        return new HelperProgress(r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64());
    }

    public static void WriteError(BinaryWriter w, HelperErrorKind kind, string message)
    {
        w.Write((byte)kind);
        w.Write(message.Length > 4096 ? message[..4096] : message);
    }

    public static (HelperErrorKind Kind, string Message) ReadError(byte[] payload)
    {
        using var r = Reader(payload);
        return ((HelperErrorKind)r.ReadByte(), r.ReadString());
    }

    private static BinaryReader Reader(byte[] payload) => new(new MemoryStream(payload, writable: false), System.Text.Encoding.UTF8);
}
