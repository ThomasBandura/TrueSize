using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TrueSize.Core.Interop;

namespace TrueSize.Core.Ntfs;

/// <summary>Raw read access to an NTFS volume and its $MFT. Requires administrative rights.</summary>
internal sealed unsafe class MftVolume : IDisposable
{
    private const int ChunkBytes = 4 * 1024 * 1024;

    private const int ParallelReaders = 4;
    private const int ErrorNotSupported = 50; // winerror.h ERROR_NOT_SUPPORTED

    private readonly SafeFileHandle _handle;
    private readonly string _device;
    private readonly bool _unbuffered;

    private MftVolume(SafeFileHandle handle, string device, bool unbuffered, NTFS_VOLUME_DATA_BUFFER data)
    {
        _handle = handle;
        _device = device;
        _unbuffered = unbuffered;
        Data = data;
        ClusterSize = (int)data.BytesPerCluster;
        RecordSize = (int)data.BytesPerFileRecordSegment;
        RecordCount = data.MftValidDataLength / RecordSize;
    }

    public NTFS_VOLUME_DATA_BUFFER Data { get; }

    public int ClusterSize { get; }

    public int RecordSize { get; }

    public long RecordCount { get; }

    public List<Extent> MftExtents { get; private set; } = [];

    /// <summary>Opens the volume that contains <paramref name="volumeRoot"/> (e.g. "C:\") for raw reading.</summary>
    public static MftVolume Open(string volumeRoot, bool unbuffered = true)
    {
        char* name = stackalloc char[64];
        if (!Native.GetVolumeNameForVolumeMountPoint(volumeRoot, name, 64))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"GetVolumeNameForVolumeMountPoint failed for '{volumeRoot}'.");
        }

        // "\\?\Volume{GUID}\" -> "\\?\Volume{GUID}" opens the volume device itself.
        string device = new string(name).TrimEnd('\\');
        var handle = OpenDevice(device, unbuffered);

        NTFS_VOLUME_DATA_BUFFER data;
        if (!Native.DeviceIoControl(handle, Native.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &data, (uint)sizeof(NTFS_VOLUME_DATA_BUFFER), out _, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"FSCTL_GET_NTFS_VOLUME_DATA failed for '{device}' (not NTFS?).");
        }

        var volume = new MftVolume(handle, device, unbuffered, data);
        if (volume.RecordSize < NtfsLayout.SequenceStride || volume.RecordSize % NtfsLayout.SequenceStride != 0 || volume.ClusterSize <= 0)
        {
            volume.Dispose();
            throw new InvalidDataException($"Unsupported NTFS geometry: record size {volume.RecordSize}, cluster size {volume.ClusterSize}.");
        }

        volume.MftExtents = volume.ResolveMftExtents();
        return volume;
    }

    private static SafeFileHandle OpenDevice(string device, bool unbuffered)
    {
        SafeFileHandle handle;
        fixed (char* p = device)
        {
            // Unbuffered: reads bypass the cache manager; offsets, lengths and buffers are cluster/page aligned.
            handle = Native.CreateFile(p, Native.GENERIC_READ, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING, unbuffered ? Native.FILE_FLAG_NO_BUFFERING : 0, 0);
        }

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, $"Opening volume '{device}' failed (administrative rights required).");
        }

        return handle;
    }

    /// <summary>Reads whole clusters into <paramref name="destination"/> (length must be a multiple of the cluster size).</summary>
    public void ReadClusters(long lcn, Span<byte> destination) => ReadClusters(_handle, lcn, destination);

    // I/O on one synchronous handle is serialized by Windows, so parallel readers use their own handles.
    private void ReadClusters(SafeFileHandle handle, long lcn, Span<byte> destination)
    {
        // Plain ReadFile with an OVERLAPPED offset instead of RandomAccess: RandomAccess.Read failed with
        // ERROR_NOT_SUPPORTED on unbuffered volume handles under .NET 11 (PowerShell 7.7) while working on .NET 10.
        long offset = lcn * ClusterSize;
        int read = 0;
        fixed (byte* p = destination)
        {
            while (read < destination.Length)
            {
                long position = offset + read;
                var overlapped = new OVERLAPPED { Offset = (uint)position, OffsetHigh = (uint)(position >> 32) };
                if (!Native.ReadFile(handle, p + read, (uint)(destination.Length - read), out uint n, &overlapped))
                {
                    int error = Marshal.GetLastPInvokeError();
                    throw new Win32Exception(error, $"Reading the volume at offset {position} failed: {new Win32Exception(error).Message} (Win32 error {error}).");
                }

                if (n == 0)
                {
                    throw new IOException($"Unexpected end of volume at offset {position}.");
                }

                read += (int)n;
            }
        }
    }

    /// <summary>Opens an additional handle to the volume for a parallel reader. The caller disposes it.</summary>
    public SafeFileHandle OpenReader() => OpenDevice(_device, _unbuffered);

    /// <summary>Reads <paramref name="length"/> bytes of a nonresident stream given its extents.</summary>
    public byte[] ReadStream(List<Extent> extents, long length) => ReadStream(_handle, extents, length);

    /// <summary>Reads <paramref name="length"/> bytes of a nonresident stream using <paramref name="handle"/>.</summary>
    public byte[] ReadStream(SafeFileHandle handle, List<Extent> extents, long length)
    {
        var result = new byte[length];
        using var buffer = new AlignedBuffer(ClusterSize);
        long position = 0;
        foreach (var extent in extents.OrderBy(e => e.Vcn))
        {
            for (long c = 0; c < extent.Length && position < length; c++)
            {
                long streamOffset = (extent.Vcn + c) * ClusterSize;
                if (streamOffset >= length)
                {
                    return result;
                }

                if (extent.Lcn >= 0)
                {
                    ReadClusters(handle, extent.Lcn + c, buffer.Span);
                    int n = (int)Math.Min(ClusterSize, length - streamOffset);
                    buffer.Span[..n].CopyTo(result.AsSpan((int)streamOffset));
                }

                position = streamOffset + ClusterSize;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads all MFT records in on-disk order and invokes <paramref name="onRecord"/> with the record number and the raw
    /// record bytes (fixups not yet applied; the callback may modify the span).
    /// </summary>
    public void ReadAllRecords(RecordCallback onRecord, CancellationToken cancellationToken)
    {
        // Records never cross a cluster when the cluster size is a multiple of the record size (the usual case).
        if (ClusterSize % RecordSize == 0)
        {
            ReadAllRecordsParallel(onRecord, cancellationToken);
        }
        else
        {
            ReadAllRecordsSequential(onRecord, cancellationToken);
        }
    }

    private readonly record struct Chunk(long Lcn, int Clusters, long StreamOffset, int Bytes);

    /// <summary>
    /// Several readers (each with its own handle) fetch chunks concurrently to keep the device queue busy; the calling
    /// thread parses chunks as they arrive. Record order is irrelevant for parsing.
    /// </summary>
    private void ReadAllRecordsParallel(RecordCallback onRecord, CancellationToken cancellationToken)
    {
        long totalBytes = RecordCount * RecordSize;
        int chunkClusters = Math.Max(1, ChunkBytes / ClusterSize);
        var chunks = new List<Chunk>();
        foreach (var extent in MftExtents)
        {
            for (long c = 0; c < extent.Length; c += chunkClusters)
            {
                long streamOffset = (extent.Vcn + c) * ClusterSize;
                if (streamOffset >= totalBytes)
                {
                    break;
                }

                int clusters = (int)Math.Min(chunkClusters, extent.Length - c);
                int bytes = (int)Math.Min((long)clusters * ClusterSize, totalBytes - streamOffset);
                if (extent.Lcn >= 0)
                {
                    chunks.Add(new Chunk(extent.Lcn + c, clusters, streamOffset, bytes));
                }
            }
        }

        int readers = Math.Min(ParallelReaders, Math.Max(1, chunks.Count));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var buffers = new List<AlignedBuffer>();
        using var free = new System.Collections.Concurrent.BlockingCollection<AlignedBuffer>();
        using var ready = new System.Collections.Concurrent.BlockingCollection<(Chunk Chunk, AlignedBuffer Buffer)>();
        for (int i = 0; i < readers + 2; i++)
        {
            var buffer = new AlignedBuffer(chunkClusters * ClusterSize);
            buffers.Add(buffer);
            free.Add(buffer);
        }

        int next = -1;
        int running = readers;
        Exception? fault = null;
        var tasks = new Task[readers];
        for (int r = 0; r < readers; r++)
        {
            tasks[r] = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        using var handle = OpenDevice(_device, _unbuffered);
                        int i;
                        while ((i = Interlocked.Increment(ref next)) < chunks.Count)
                        {
                            var chunk = chunks[i];
                            var buffer = free.Take(token);
                            ReadClusters(handle, chunk.Lcn, buffer.Span[..(chunk.Clusters * ClusterSize)]);
                            ready.Add((chunk, buffer), token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref fault, ex, null);
                        linked.Cancel();
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref running) == 0)
                        {
                            ready.CompleteAdding();
                        }
                    }
                },
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        try
        {
            foreach (var (chunk, buffer) in ready.GetConsumingEnumerable(token))
            {
                var span = buffer.Span[..chunk.Bytes];
                for (int offset = 0; offset + RecordSize <= span.Length; offset += RecordSize)
                {
                    onRecord((chunk.StreamOffset + offset) / RecordSize, span.Slice(offset, RecordSize));
                }

                free.Add(buffer);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A reader failed and cancelled the linked token; reported below.
        }
        finally
        {
            linked.Cancel();
            Task.WaitAll(tasks.Select(t => t.ContinueWith(static _ => { }, TaskScheduler.Default)).ToArray());
            foreach (var buffer in buffers)
            {
                buffer.Dispose();
            }
        }

        if (fault is not null)
        {
            throw new IOException("Reading the MFT failed: " + fault.Message, fault);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ReadAllRecordsSequential(RecordCallback onRecord, CancellationToken cancellationToken)
    {
        long totalBytes = RecordCount * RecordSize;
        int chunkClusters = Math.Max(1, ChunkBytes / ClusterSize);
        using var buffer = new AlignedBuffer(chunkClusters * ClusterSize);
        var carry = new byte[RecordSize];
        int carryFilled = 0;

        foreach (var extent in MftExtents)
        {
            long streamStart = extent.Vcn * ClusterSize;
            for (long c = 0; c < extent.Length; c += chunkClusters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long chunkStream = streamStart + (c * ClusterSize);
                if (chunkStream >= totalBytes)
                {
                    return;
                }

                int clusters = (int)Math.Min(chunkClusters, extent.Length - c);
                int bytes = (int)Math.Min((long)clusters * ClusterSize, totalBytes - chunkStream);
                var chunk = buffer.Span[..(clusters * ClusterSize)];
                if (extent.Lcn < 0)
                {
                    chunk.Clear();
                }
                else
                {
                    ReadClusters(extent.Lcn + c, chunk);
                }

                chunk = chunk[..bytes];
                long position = chunkStream;
                int offset = 0;

                // Complete a record that spans the previous chunk (only when clusters are smaller than records).
                if (carryFilled > 0)
                {
                    int take = Math.Min(RecordSize - carryFilled, chunk.Length);
                    chunk[..take].CopyTo(carry.AsSpan(carryFilled));
                    carryFilled += take;
                    offset = take;
                    if (carryFilled == RecordSize)
                    {
                        onRecord((position - (carryFilled - take)) / RecordSize, carry);
                        carryFilled = 0;
                    }
                }

                while (offset + RecordSize <= chunk.Length)
                {
                    onRecord((position + offset) / RecordSize, chunk.Slice(offset, RecordSize));
                    offset += RecordSize;
                }

                if (offset < chunk.Length)
                {
                    chunk[offset..].CopyTo(carry);
                    carryFilled = chunk.Length - offset;
                }
            }
        }
    }

    public delegate void RecordCallback(long recordNumber, Span<byte> record);

    /// <summary>Reads one record by number using the known MFT extents, with fixups applied. Returns null if not mapped.</summary>
    private byte[]? ReadRecord(long recordNumber, List<Extent> extents)
    {
        long streamOffset = recordNumber * RecordSize;
        var record = new byte[RecordSize];
        int filled = 0;
        using var buffer = new AlignedBuffer(ClusterSize);
        while (filled < RecordSize)
        {
            long vcn = (streamOffset + filled) / ClusterSize;
            int within = (int)((streamOffset + filled) % ClusterSize);
            var extent = extents.FirstOrDefault(e => vcn >= e.Vcn && vcn < e.Vcn + e.Length && e.Lcn >= 0);
            if (extent.Length == 0)
            {
                return null;
            }

            ReadClusters(extent.Lcn + (vcn - extent.Vcn), buffer.Span);
            int n = Math.Min(ClusterSize - within, RecordSize - filled);
            buffer.Span.Slice(within, n).CopyTo(record.AsSpan(filled));
            filled += n;
        }

        return NtfsRecord.HasFileSignature(record) && NtfsRecord.ApplyFixup(record) ? record : null;
    }

    /// <summary>
    /// Collects the extents of $MFT's unnamed $DATA attribute: from record 0 and, if $MFT is fragmented beyond one
    /// record, from extension records listed in its $ATTRIBUTE_LIST.
    /// </summary>
    private List<Extent> ResolveMftExtents()
    {
        // Record 0 starts at MftStartLcn.
        int clusters = (RecordSize + ClusterSize - 1) / ClusterSize;
        using var first = new AlignedBuffer(clusters * ClusterSize);
        try
        {
            ReadClusters(Data.MftStartLcn, first.Span);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNotSupported)
        {
            // Observed in the Microsoft Store (MSIX-packaged) PowerShell 7.7: opening the volume succeeds, every read fails
            // with ERROR_NOT_SUPPORTED, while child processes of the same session read fine.
            throw new NotSupportedException(
                "Raw volume reads are not supported in this process (ERROR_NOT_SUPPORTED). This happens in packaged (Microsoft Store) " +
                "PowerShell; use PowerShell installed via MSI/winget for the fast scanner.", ex);
        }

        var record0 = first.Span[..RecordSize].ToArray();
        if (!NtfsRecord.HasFileSignature(record0) || !NtfsRecord.ApplyFixup(record0))
        {
            throw new InvalidDataException("$MFT record 0 is invalid.");
        }

        var extents = new List<Extent>();
        var extensionRecords = new List<long>();
        foreach (var attribute in NtfsRecord.Attributes(record0))
        {
            if (attribute.TypeCode == NtfsLayout.DATA && attribute.Name.IsEmpty)
            {
                attribute.DecodeMappingPairs(extents);
            }
            else if (attribute.TypeCode == NtfsLayout.ATTRIBUTE_LIST)
            {
                byte[] list;
                if (attribute.IsResident)
                {
                    list = attribute.Value.ToArray();
                }
                else
                {
                    var listExtents = new List<Extent>();
                    attribute.DecodeMappingPairs(listExtents);
                    list = ReadStream(listExtents, attribute.FileSize);
                }

                CollectDataSegments(list, extensionRecords);
            }
        }

        // Extension records may only be reachable through extents found in other extension records.
        var pending = extensionRecords.Where(r => r != NtfsLayout.MftRecord).Distinct().ToList();
        bool progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (long recordNumber in pending.ToArray())
            {
                var record = ReadRecord(recordNumber, extents);
                if (record is null)
                {
                    continue;
                }

                foreach (var attribute in NtfsRecord.Attributes(record))
                {
                    if (attribute.TypeCode == NtfsLayout.DATA && attribute.Name.IsEmpty)
                    {
                        attribute.DecodeMappingPairs(extents);
                    }
                }

                pending.Remove(recordNumber);
                progress = true;
            }
        }

        if (pending.Count > 0)
        {
            throw new InvalidDataException("$MFT extension records could not be located.");
        }

        extents.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
        return extents;
    }

    private static void CollectDataSegments(ReadOnlySpan<byte> list, List<long> segments)
    {
        int offset = 0;
        while (offset + 24 <= list.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(list[(offset + NtfsLayout.ListTypeCode)..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(list[(offset + NtfsLayout.ListRecordLength)..]);
            if (length == 0 || type == NtfsLayout.END)
            {
                break;
            }

            if (type == NtfsLayout.DATA)
            {
                segments.Add(NtfsLayout.ReadSegmentReference(list[(offset + NtfsLayout.ListSegmentReference)..]).Segment);
            }

            offset += length;
        }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>Page-aligned native buffer, required for unbuffered volume reads.</summary>
    internal sealed class AlignedBuffer : IDisposable
    {
        private readonly byte* _pointer;

        public AlignedBuffer(int length)
        {
            Length = length;
            _pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, 4096);
        }

        public int Length { get; }

        public Span<byte> Span => new(_pointer, Length);

        public void Dispose() => NativeMemory.AlignedFree(_pointer);
    }
}
