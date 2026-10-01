// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// One log file plus its open-addressing index (key hash → latest record offset in this file).
/// </summary>
/// <remarks>
/// The index has a fixed capacity and is only mutated by the single writer through <see cref="Publish"/>; each
/// slot is one <c>ulong</c> written with <see cref="Volatile.Write(ref ulong, ulong)"/>, so concurrent readers
/// never see a torn slot. A reader verifies the key against the record it lands on, so a tag collision only
/// costs an extra probe. The lease count keeps the file and table alive for readers and write batches that
/// pinned the generation; the log holds its own lease until the generation has been merged into RocksDB, and the
/// last release deletes the file unless <see cref="PreserveOnDispose"/> was set (shutdown before the merge).
/// </remarks>
internal sealed unsafe class TrieNodeLogGeneration : RefCountingDisposable
{
    private const int MinCapacity = 1024;

    private static long _aliveCount;
    private static long _aliveIndexBytes;

    private readonly ulong* _slots;
    private readonly int _mask;
    private int _preserve;

    public ulong Number { get; }
    public string Path { get; }
    public SafeFileHandle Handle { get; }
    public int Capacity { get; }

    /// <summary>Number of occupied index slots.</summary>
    public int Occupied { get; private set; }

    /// <summary>End of the committed, reader-visible records.</summary>
    public long Frontier { get; private set; }

    /// <summary>End of the appended records including the open batch's; writer-only.</summary>
    public long WriteFrontier { get; set; }

    public bool IsSealed { get; set; }

    public bool IsFlushed { get; set; }

    public TrieNodeLogGeneration(ulong number, string path, int capacity)
    {
        Number = number;
        Path = path;
        Capacity = Math.Max(MinCapacity, (int)BitOperations.RoundUpToPowerOf2((uint)capacity));
        _mask = Capacity - 1;
        Handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        // Zeroed native memory so the table never touches the GC heap; freed exactly once in CleanUp.
        _slots = (ulong*)NativeMemory.AllocZeroed((nuint)Capacity, sizeof(ulong));
        Interlocked.Increment(ref _aliveCount);
        Interlocked.Add(ref _aliveIndexBytes, IndexBytes);
    }

    /// <summary>Generations whose file and index have not been released yet, merged-but-pinned ones included.</summary>
    public static long AliveCount => Volatile.Read(ref _aliveCount);

    public static long AliveIndexBytes => Volatile.Read(ref _aliveIndexBytes);

    public long IndexBytes => (long)Capacity * sizeof(ulong);

    public static int CapacityFor(long generationBytes) => (int)Math.Min(int.MaxValue / 2, generationBytes / 64);

    public bool TryAcquire() => TryAcquireLease();

    public void PreserveOnDispose() => Volatile.Write(ref _preserve, 1);

    public int HomeIndex(ulong hash) => (int)(hash & (ulong)_mask);

    public int NextIndex(int index) => (index + 1) & _mask;

    public ulong ReadSlot(int index) => Volatile.Read(ref _slots[index]);

    /// <summary>Points slot <paramref name="index"/> (empty, or already holding this key) at the record at <paramref name="offset"/>.</summary>
    public void Publish(int index, ulong hash, long offset)
    {
        if (_slots[index] == 0) Occupied++;
        Volatile.Write(ref _slots[index], TrieNodeLogRecord.PackSlot(hash, offset));
    }

    /// <summary>Finds the slot that references the record at <paramref name="offset"/>, i.e. whether it is the latest for its key.</summary>
    public bool IsLatest(ulong hash, long offset)
    {
        for (int index = HomeIndex(hash); ; index = NextIndex(index))
        {
            ulong slot = ReadSlot(index);
            if (slot == 0) return false;
            if (TrieNodeLogRecord.SlotOffset(slot) == offset) return true;
        }
    }

    /// <summary>
    /// Probes for <paramref name="key"/>. On a hit, <paramref name="buffer"/> holds the start of the record (header,
    /// key and as much of the value as fit), <paramref name="header"/> its header and <paramref name="index"/> its slot.
    /// On a miss <paramref name="index"/> is the first empty slot on the probe path.
    /// </summary>
    public bool TryLocate(ulong hash, ReadOnlySpan<byte> key, Span<byte> buffer, out TrieNodeLogRecord header, out int index, out long offset, out int bytesRead)
    {
        for (index = HomeIndex(hash); ; index = NextIndex(index))
        {
            ulong slot = ReadSlot(index);
            if (slot == 0) break;
            if (!TrieNodeLogRecord.SlotTagMatches(slot, hash)) continue;

            offset = TrieNodeLogRecord.SlotOffset(slot);
            bytesRead = ReadAt(offset, buffer);
            if (bytesRead < TrieNodeLogRecord.HeaderLength) continue;
            header = TrieNodeLogRecord.Read(buffer);
            if (header.KeyLength == key.Length && bytesRead >= TrieNodeLogRecord.HeaderLength + key.Length
                && buffer.Slice(TrieNodeLogRecord.HeaderLength, key.Length).SequenceEqual(key))
                return true;
            Metrics.RecordTrieNodeLogIndexFalseMatch();
        }

        header = default;
        offset = -1;
        bytesRead = 0;
        return false;
    }

    public int ReadAt(long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(Handle, destination[total..], offset + total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    /// <summary>Materializes the value of the record at <paramref name="offset"/> whose first <paramref name="bytesRead"/> bytes are in <paramref name="buffer"/>.</summary>
    public byte[] ReadValue(long offset, in TrieNodeLogRecord header, ReadOnlySpan<byte> buffer, int bytesRead)
    {
        int valueStart = TrieNodeLogRecord.HeaderLength + header.KeyLength;
        byte[] value = new byte[header.ValueLength];
        int available = Math.Min(header.ValueLength, Math.Max(0, bytesRead - valueStart));
        buffer.Slice(valueStart, available).CopyTo(value);
        if (available < header.ValueLength && ReadAt(offset + valueStart + available, value.AsSpan(available)) != header.ValueLength - available)
            throw new IOException($"Short read of trie node log record at {Path}:{offset}");
        return value;
    }

    public void Write(long offset, ReadOnlySpan<byte> data) => RandomAccess.Write(Handle, data, offset);

    public void Fsync() => RandomAccess.FlushToDisk(Handle);

    public void Truncate(long length)
    {
        RandomAccess.SetLength(Handle, length);
        WriteFrontier = length;
        Frontier = length;
    }

    public void PublishFrontier(long frontier) => Frontier = frontier;

    protected override void CleanUp()
    {
        Handle.Dispose();
        NativeMemory.Free(_slots);
        Interlocked.Decrement(ref _aliveCount);
        Interlocked.Add(ref _aliveIndexBytes, -IndexBytes);
        if (Volatile.Read(ref _preserve) == 0)
        {
            try { File.Delete(Path); } catch { /* best-effort */ }
        }
    }
}
