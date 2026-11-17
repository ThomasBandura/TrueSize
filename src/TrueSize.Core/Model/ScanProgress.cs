namespace TrueSize.Core.Model;

/// <summary>Live counters, safe to poll from another thread while a scan runs.</summary>
public sealed class ScanProgress
{
    private long _files;
    private long _directories;
    private long _bytes;
    private long _errors;

    public long Files => Volatile.Read(ref _files);
    public long Directories => Volatile.Read(ref _directories);
    public long AllocatedBytes => Volatile.Read(ref _bytes);
    public long Errors => Volatile.Read(ref _errors);

    internal void Add(long files, long directories, long bytes)
    {
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _directories, directories);
        Interlocked.Add(ref _bytes, bytes);
    }

    internal void AddError() => Interlocked.Increment(ref _errors);
}

public sealed record ScanError(string Path, int Win32Error, string Operation)
{
    public string Message => new System.ComponentModel.Win32Exception(Win32Error).Message;
}
