// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat;

/// <summary>
/// Append-only pinned-slab byte arena for snapshot node records (keccak + RLP). Records are
/// immutable once written; overwritten or tombstoned records leak their bytes until
/// <see cref="Release"/>.
/// </summary>
/// <remarks>
/// Two append protocols share one slab array: the single-threaded <see cref="Append"/> (owned
/// <see cref="Cursor"/>) and the lock-free <see cref="AppendShared"/> (a caller-held packed
/// <see cref="long"/> cursor). Address-owned flat node storage uses a small fixed set of shared
/// cursors so thousands of addresses pack into a handful of partial slabs instead of one slab each.
/// </remarks>
public sealed class SlabArena
{
    public const int SlabSize = 1 << 20;
    private const int MaxPooledSlabs = 8192;
    private const int MinRetainedSlabs = 1024;
    private const int TrimEpochReleases = 256;

    /// <summary>Empty shared cursor: slab index -1 forces the first append to grow a slab.</summary>
    public const long EmptySharedCursor = -1L << 32;

    public static bool DebugChecks;

    private static readonly ConcurrentStack<byte[]> s_slabPool = new();
    private static int s_pooledCount;
    private static int s_inUseCount;
    private static int s_inUseHighWater;
    private static int s_releasesSinceTrim;

    private readonly object _growLock = new();
    private byte[][] _slabs = [];
    private int _slabCount;
    private int _generation;
    private long _bytesAppended;

    public int Generation => Volatile.Read(ref _generation);
    public long BytesAppended => Volatile.Read(ref _bytesAppended);
    public long BytesReserved => (long)Volatile.Read(ref _slabCount) * SlabSize;

    /// <summary>Append cursor; touched only under the owning shard's lock or by a single-threaded build.</summary>
    public struct Cursor
    {
        internal int Slab;
        internal int Offset;

        public static Cursor Empty => new() { Slab = -1 };
    }

    public SlabHandle Append(ref Cursor cursor, Hash256? keccak, ReadOnlySpan<byte> rlp, SlabFlags flags)
    {
        if (keccak is not null) flags |= SlabFlags.HasKeccak;
        int keccakLen = keccak is not null ? 32 : 0;
        int recordLen = keccakLen + rlp.Length;
        if (recordLen > SlabSize) throw new InvalidOperationException($"Node record of {recordLen} bytes exceeds the slab size");

        if (cursor.Slab < 0 || cursor.Offset + recordLen > SlabSize)
        {
            (cursor.Slab, cursor.Offset) = AddSlab();
        }

        byte[] slab = Volatile.Read(ref _slabs)[cursor.Slab];
        int offset = cursor.Offset;
        if (keccakLen != 0) keccak!.Bytes.CopyTo(slab.AsSpan(offset, 32));
        if (rlp.Length != 0) rlp.CopyTo(slab.AsSpan(offset + keccakLen, rlp.Length));

        cursor.Offset = offset + recordLen;
        Interlocked.Add(ref _bytesAppended, recordLen);
        return SlabHandle.Create(cursor.Slab, offset, rlp.Length, flags);
    }

    /// <summary>
    /// Lock-free append against a caller-held packed cursor (slabIndex:32 | offset:32). Each cursor
    /// grabs slabs exclusively via CAS, so records reserved through the same cursor never overlap and
    /// records reserved through different cursors land in different slabs. The record bytes are written
    /// after the region is reserved; a reader must only look up a handle once it was published, which
    /// happens-after the write.
    /// </summary>
    public SlabHandle AppendShared(ref long cursor, Hash256? keccak, ReadOnlySpan<byte> rlp, SlabFlags flags)
    {
        if (keccak is not null) flags |= SlabFlags.HasKeccak;
        int keccakLen = keccak is not null ? 32 : 0;
        int recordLen = keccakLen + rlp.Length;
        if (recordLen > SlabSize) throw new InvalidOperationException($"Node record of {recordLen} bytes exceeds the slab size");

        while (true)
        {
            long current = Volatile.Read(ref cursor);
            int slabIndex = (int)(current >> 32);
            int offset = (int)(uint)current;
            if (slabIndex < 0 || offset + recordLen > SlabSize)
            {
                GrowSharedCursor(ref cursor, current);
                continue;
            }

            long next = ((long)slabIndex << 32) | (uint)(offset + recordLen);
            if (Interlocked.CompareExchange(ref cursor, next, current) != current) continue;

            byte[] slab = Volatile.Read(ref _slabs)[slabIndex];
            if (keccakLen != 0) keccak!.Bytes.CopyTo(slab.AsSpan(offset, 32));
            if (rlp.Length != 0) rlp.CopyTo(slab.AsSpan(offset + keccakLen, rlp.Length));

            Interlocked.Add(ref _bytesAppended, recordLen);
            return SlabHandle.Create(slabIndex, offset, rlp.Length, flags);
        }
    }

    private void GrowSharedCursor(ref long cursor, long observed)
    {
        lock (_growLock)
        {
            // Another appender already advanced this cursor to a fresh slab; retry against it.
            if (Volatile.Read(ref cursor) != observed) return;

            byte[] slab = RentSlab();
            byte[][] slabs = _slabs;
            if (_slabCount == slabs.Length)
            {
                byte[][] grown = new byte[Math.Max(4, slabs.Length * 2)][];
                Array.Copy(slabs, grown, slabs.Length);
                grown[_slabCount] = slab;
                Volatile.Write(ref _slabs, grown);
            }
            else
            {
                slabs[_slabCount] = slab;
            }

            int index = _slabCount;
            Volatile.Write(ref _slabCount, index + 1);
            // No record may start at (slab 0, offset 0): SlabHandle.None must stay unambiguous.
            int startOffset = index == 0 ? 1 : 0;
            Volatile.Write(ref cursor, ((long)index << 32) | (uint)startOffset);
        }
    }

    private (int Slab, int Offset) AddSlab()
    {
        lock (_growLock)
        {
            byte[] slab = RentSlab();
            byte[][] slabs = _slabs;
            if (_slabCount == slabs.Length)
            {
                byte[][] grown = new byte[Math.Max(4, slabs.Length * 2)][];
                Array.Copy(slabs, grown, slabs.Length);
                grown[_slabCount] = slab;
                Volatile.Write(ref _slabs, grown);
            }
            else
            {
                slabs[_slabCount] = slab;
            }

            int index = _slabCount;
            Volatile.Write(ref _slabCount, index + 1);
            // No record may start at (slab 0, offset 0): SlabHandle.None must stay unambiguous.
            return (index, index == 0 ? 1 : 0);
        }
    }

    /// <summary>Copies the record out; <paramref name="generation"/> must have been sampled
    /// BEFORE the handle was read. False = miss.</summary>
    public bool TryReadCopy(SlabHandle handle, int generation, out Hash256? keccak, out byte[]? rlpCopy)
    {
        keccak = null;
        rlpCopy = null;
        if (handle.IsNone) return false;

        byte[][] slabs = Volatile.Read(ref _slabs);
        int slabIndex = handle.SlabIndex;
        if ((uint)slabIndex >= (uint)Volatile.Read(ref _slabCount) || slabIndex >= slabs.Length) return false;

        int keccakLen = (handle.Flags & SlabFlags.HasKeccak) != 0 ? 32 : 0;
        int recordLen = keccakLen + handle.RlpLength;
        if (handle.Offset + recordLen > SlabSize) return false;

        byte[] slab = slabs[slabIndex];
        Span<byte> keccakBytes = stackalloc byte[32];
        if (keccakLen != 0) slab.AsSpan(handle.Offset, 32).CopyTo(keccakBytes);
        byte[]? rlp = handle.RlpLength != 0 ? slab.AsSpan(handle.Offset + keccakLen, handle.RlpLength).ToArray() : null;

        if (Volatile.Read(ref _generation) != generation)
        {
            Metrics.SlabReadGenerationMisses++;
            return false;
        }

        if (keccakLen != 0) keccak = new Hash256(keccakBytes);
        rlpCopy = rlp;
        return true;
    }

    /// <summary>Raw span read; only valid while the caller holds a lease keeping the content quiescent.</summary>
    internal void ReadSpan(SlabHandle handle, out ReadOnlySpan<byte> keccakOrEmpty, out ReadOnlySpan<byte> rlp)
    {
        byte[] slab = Volatile.Read(ref _slabs)[handle.SlabIndex];
        int keccakLen = (handle.Flags & SlabFlags.HasKeccak) != 0 ? 32 : 0;
        keccakOrEmpty = keccakLen != 0 ? slab.AsSpan(handle.Offset, 32) : default;
        rlp = handle.RlpLength != 0 ? slab.AsSpan(handle.Offset + keccakLen, handle.RlpLength) : default;
    }

    /// <summary>Returns every slab to the pool. Only valid at the content's quiescent points (reset/dispose).</summary>
    public void Release()
    {
        Interlocked.Increment(ref _generation);

        byte[][] slabs;
        int count;
        lock (_growLock)
        {
            slabs = _slabs;
            count = _slabCount;
            Volatile.Write(ref _slabs, Array.Empty<byte[]>());
            Volatile.Write(ref _slabCount, 0);
            Interlocked.Exchange(ref _bytesAppended, 0);
        }

        for (int i = 0; i < count; i++)
        {
            byte[] slab = slabs[i];
            if (DebugChecks) slab.AsSpan().Fill(0xDE);
            ReturnSlab(slab);
        }
    }

    private static byte[] RentSlab()
    {
        int inUse = Interlocked.Increment(ref s_inUseCount);
        InterlockedMax(ref s_inUseHighWater, inUse);
        if (s_slabPool.TryPop(out byte[]? pooled))
        {
            Interlocked.Decrement(ref s_pooledCount);
            Metrics.SlabPoolCachedSlabs = s_pooledCount;
            return pooled;
        }

        return GC.AllocateUninitializedArray<byte>(SlabSize, pinned: true);
    }

    private static void ReturnSlab(byte[] slab)
    {
        Interlocked.Decrement(ref s_inUseCount);
        if (Volatile.Read(ref s_pooledCount) < MaxPooledSlabs)
        {
            s_slabPool.Push(slab);
            Interlocked.Increment(ref s_pooledCount);
        }

        if (Interlocked.Increment(ref s_releasesSinceTrim) >= TrimEpochReleases)
        {
            Interlocked.Exchange(ref s_releasesSinceTrim, 0);
            int keep = Math.Max(Interlocked.Exchange(ref s_inUseHighWater, Volatile.Read(ref s_inUseCount)), MinRetainedSlabs);
            while (Volatile.Read(ref s_pooledCount) > keep && s_slabPool.TryPop(out _))
            {
                Interlocked.Decrement(ref s_pooledCount);
            }
        }

        Metrics.SlabPoolCachedSlabs = s_pooledCount;
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        while ((current = Volatile.Read(ref location)) < value &&
               Interlocked.CompareExchange(ref location, value, current) != current)
        {
        }
    }
}
