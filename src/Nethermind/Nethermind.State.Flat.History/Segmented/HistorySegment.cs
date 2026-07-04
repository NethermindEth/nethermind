// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.History.Segmented;

/// <summary>
/// An immutable, memory-mapped Approach-2 segment for one flat domain over a block range. Holds, for each distinct
/// flat key seen in the range, the sorted set of change-blocks (Elias-Fano encoded) and the matching values packed
/// in rank order. A read binary-searches the sorted key table, runs <see cref="EliasFano.Reader.Predecessor"/> to
/// get the rank of the change at/before the query block, then slices that value out of the key's packed run —
/// no per-change keys are stored.
/// </summary>
/// <remarks>
/// Single-file layout (all little-endian); region offsets are stored in the header and also derivable from the
/// fixed region order:
/// <code>
/// header    80 bytes (magic, version, keyLen, fromBlock, toBlock, entryCount K, region offsets, fileLength)
/// keys      K · keyLen                    sorted ascending
/// efDir     (K+1) · u64                   entity j's EF blob = efData[efDir[j] .. efDir[j+1])
/// efData    concatenated EF blobs
/// valDir    (K+1) · u64                   entity j's value run = valData[valDir[j] .. valDir[j+1])
/// valData   concatenated runs; a run is [(m+1)·u32 value offsets][value bytes], m = change count
/// </code>
/// </remarks>
public sealed unsafe class HistorySegment : IDisposable
{
    private const uint Magic = 0x3153484E; // "NHS1"
    private const ushort Version = 1;
    private const int HeaderBytes = 80;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;

    private readonly int _keyLen;
    private readonly int _count;
    private readonly long _keysOffset;
    private readonly long _efDirOffset;
    private readonly long _efOffset;
    private readonly long _valDirOffset;
    private readonly long _valOffset;

    public ulong FromBlock { get; }
    public ulong ToBlock { get; }
    public int Count => _count;
    public string Path { get; }

    public HistorySegment(string path)
    {
        Path = path;
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _base = ptr + _view.PointerOffset;

        ReadOnlySpan<byte> header = new(_base, HeaderBytes);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException($"'{path}' is not a {nameof(HistorySegment)} (bad magic).");

        _keyLen = header[6];
        FromBlock = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        ToBlock = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        _count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        _keysOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
        _efDirOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
        _efOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[48..]);
        _valDirOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[56..]);
        _valOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[64..]);
    }

    /// <summary>
    /// Reads the value of <paramref name="flatKey"/> as of <paramref name="block"/> from this segment alone.
    /// Returns -1 when this segment holds no change for the key at/before the block (the caller must consult
    /// older segments), 0 for a deletion tombstone, otherwise the value length written to <paramref name="outBuffer"/>.
    /// </summary>
    public int TryGetAt(scoped ReadOnlySpan<byte> flatKey, ulong block, Span<byte> outBuffer, out ulong foundAtBlock)
    {
        foundAtBlock = 0;
        if (!TryFindOrdinal(flatKey, out int j)) return -1;

        EliasFano.Reader ef = new(EfBlob(j));
        if (!ef.Predecessor(block, out int rank, out ulong changeBlock)) return -1;

        foundAtBlock = changeBlock;
        ReadOnlySpan<byte> value = ValueAt(j, rank, ef.Count);
        value.CopyTo(outBuffer);
        return value.Length;
    }

    /// <summary>Whether <paramref name="flatKey"/> recorded any change in <c>(afterExclusive, atOrBefore]</c> within this segment.</summary>
    public bool HasChangeInRange(scoped ReadOnlySpan<byte> flatKey, ulong afterExclusive, ulong atOrBefore)
    {
        if (afterExclusive >= atOrBefore || !TryFindOrdinal(flatKey, out int j)) return false;
        EliasFano.Reader ef = new(EfBlob(j));
        return ef.Predecessor(atOrBefore, out _, out ulong changeBlock) && changeBlock > afterExclusive;
    }

    public ReadOnlySpan<byte> KeyAt(int ordinal) => new(_base + _keysOffset + (long)ordinal * _keyLen, _keyLen);

    /// <summary>Materializes entity <paramref name="ordinal"/> as change-blocks + values, for merging segments.</summary>
    public HistoryChangeEntry ReadEntry(int ordinal)
    {
        EliasFano.Reader ef = new(EfBlob(ordinal));
        int m = ef.Count;
        ulong[] blocks = new ulong[m];
        byte[][] values = new byte[m][];
        for (int i = 0; i < m; i++)
        {
            blocks[i] = ef[i];
            values[i] = ValueAt(ordinal, i, m).ToArray();
        }
        return new HistoryChangeEntry(KeyAt(ordinal).ToArray(), blocks, values);
    }

    // Binary search of the fixed-width, ascending key table.
    private bool TryFindOrdinal(scoped ReadOnlySpan<byte> flatKey, out int ordinal)
    {
        if (flatKey.Length != _keyLen)
        {
            ordinal = -1;
            return false;
        }

        int lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            int cmp = KeyAt(mid).SequenceCompareTo(flatKey);
            if (cmp == 0) { ordinal = mid; return true; }
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }
        ordinal = -1;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> EfBlob(int j)
    {
        byte* dir = _base + _efDirOffset + (long)j * sizeof(ulong);
        ulong start = ReadU64(dir);
        ulong end = ReadU64(dir + sizeof(ulong));
        return new ReadOnlySpan<byte>(_base + _efOffset + (long)start, (int)(end - start));
    }

    private ReadOnlySpan<byte> ValueAt(int j, int rank, int changeCount)
    {
        byte* dir = _base + _valDirOffset + (long)j * sizeof(ulong);
        long runStart = _valOffset + (long)ReadU64(dir);
        byte* offsets = _base + runStart;                 // (changeCount + 1) u32 offsets
        byte* bytes = offsets + (long)(changeCount + 1) * sizeof(uint);
        uint from = ReadU32(offsets + (long)rank * sizeof(uint));
        uint to = ReadU32(offsets + (long)(rank + 1) * sizeof(uint));
        return new ReadOnlySpan<byte>(bytes + from, (int)(to - from));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadU64(byte* p) => BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(p, sizeof(ulong)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32(byte* p) => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(p, sizeof(uint)));

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }

    /// <summary>
    /// Writes an immutable segment for <paramref name="entries"/> (which MUST be sorted ascending by key and share a
    /// fixed <paramref name="keyLen"/>) covering <c>[fromBlock, toBlock]</c>.
    /// </summary>
    public static void Write(string path, int keyLen, ulong fromBlock, ulong toBlock, IReadOnlyList<HistoryChangeEntry> entries)
    {
        int k = entries.Count;
        byte[][] efBlobs = new byte[k][];
        byte[][] valRuns = new byte[k][];
        long efTotal = 0, valTotal = 0;
        for (int j = 0; j < k; j++)
        {
            HistoryChangeEntry e = entries[j];
            efBlobs[j] = new byte[EliasFano.GetEncodedLength(e.Blocks)];
            EliasFano.Encode(e.Blocks, efBlobs[j]);
            valRuns[j] = BuildValueRun(e.Values);
            efTotal += efBlobs[j].Length;
            valTotal += valRuns[j].Length;
        }

        long keysOffset = HeaderBytes;
        long efDirOffset = keysOffset + (long)k * keyLen;
        long efOffset = efDirOffset + (long)(k + 1) * sizeof(ulong);
        long valDirOffset = efOffset + efTotal;
        long valOffset = valDirOffset + (long)(k + 1) * sizeof(ulong);
        long fileLength = valOffset + valTotal;

        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);

        Span<byte> header = stackalloc byte[HeaderBytes];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        header[6] = (byte)keyLen;
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], fromBlock);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], toBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)k);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)keysOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], (ulong)efDirOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)efOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[56..], (ulong)valDirOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[64..], (ulong)valOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], (ulong)fileLength);
        stream.Write(header);

        for (int j = 0; j < k; j++) stream.Write(entries[j].Key);
        WriteDirectory(stream, efBlobs);
        for (int j = 0; j < k; j++) stream.Write(efBlobs[j]);
        WriteDirectory(stream, valRuns);
        for (int j = 0; j < k; j++) stream.Write(valRuns[j]);
    }

    private static void WriteDirectory(Stream stream, byte[][] blobs)
    {
        Span<byte> slot = stackalloc byte[sizeof(ulong)];
        ulong cursor = 0;
        foreach (byte[] blob in blobs)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(slot, cursor);
            stream.Write(slot);
            cursor += (ulong)blob.Length;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(slot, cursor); // trailing sentinel
        stream.Write(slot);
    }

    private static byte[] BuildValueRun(byte[][] values)
    {
        int m = values.Length;
        int tableBytes = (m + 1) * sizeof(uint);
        int bytesTotal = 0;
        foreach (byte[] v in values) bytesTotal += v.Length;

        byte[] run = new byte[tableBytes + bytesTotal];
        Span<byte> table = run.AsSpan(0, tableBytes);
        Span<byte> body = run.AsSpan(tableBytes);
        uint cursor = 0;
        for (int i = 0; i < m; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table[(i * sizeof(uint))..], cursor);
            values[i].CopyTo(body[(int)cursor..]);
            cursor += (uint)values[i].Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(table[(m * sizeof(uint))..], cursor);
        return run;
    }
}

/// <summary>A flat key with its full sorted change history within a segment: the value bytes for <c>Blocks[i]</c> are
/// <c>Values[i]</c> (an empty array is a deletion tombstone).</summary>
public sealed class HistoryChangeEntry(byte[] key, ulong[] blocks, byte[][] values)
{
    public byte[] Key { get; } = key;
    public ulong[] Blocks { get; } = blocks;
    public byte[][] Values { get; } = values;
}
