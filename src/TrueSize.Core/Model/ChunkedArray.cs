namespace TrueSize.Core.Model;

/// <summary>Append-only storage in fixed-size chunks; growing never copies existing elements.</summary>
internal sealed class ChunkedArray<T>
{
    private const int Shift = 16;
    private const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;

    private T[][] _chunks = new T[16][];
    private int _chunkCount;

    public void EnsureCapacity(int capacity)
    {
        int needed = (int)(((long)capacity + Mask) >> Shift);
        if (needed > _chunks.Length)
        {
            Array.Resize(ref _chunks, Math.Max(needed, _chunks.Length * 2));
        }

        while (_chunkCount < needed)
        {
            _chunks[_chunkCount++] = new T[ChunkSize];
        }
    }

    public ref T this[int index] => ref _chunks[index >> Shift][index & Mask];

    /// <summary>Copies the first <paramref name="count"/> elements into a flat array and releases the chunks.</summary>
    public T[] ToArrayAndRelease(int count)
    {
        var result = GC.AllocateUninitializedArray<T>(count);
        int copied = 0;
        for (int c = 0; copied < count; c++)
        {
            int n = Math.Min(ChunkSize, count - copied);
            _chunks[c].AsSpan(0, n).CopyTo(result.AsSpan(copied));
            _chunks[c] = null!;
            copied += n;
        }

        _chunks = [];
        _chunkCount = 0;
        return result;
    }
}
