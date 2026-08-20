// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// An immutable, leased snapshot of the arena base tier's shard tables: one optional
/// <see cref="SortedTable"/>-bearing <see cref="ArenaFile"/> per prefix shard, for accounts and storage.
/// </summary>
/// <remarks>
/// A view pins (leases) every file it references for its whole lifetime, so readers created against it can
/// keep seeking/scanning while a fold replaces shards and drops the store's own lease on the old files.
/// Point reads and range cursors are thread-safe: they only ever read the immutable mmap regions.
/// </remarks>
internal sealed class BaseTableView : RefCountingDisposable
{
    /// <summary>One shard's immutable sorted table: the mmap-backed file, the table's byte length
    /// (the table always starts at offset 0 of its file), the generation baked into the file name, and an
    /// optional sidecar bloom filter that short-circuits misses. The filter is ref-counted alongside the
    /// file so a view outlives the fold that replaced its shard.</summary>
    internal sealed record ShardTable(ArenaFile File, long Length, long Generation, RefCountedBloomFilter? Filter = null);

    private readonly ShardTable?[] _accountShards;
    private readonly ShardTable?[] _storageShards;

    internal BaseTableView(ShardTable?[] accountShards, ShardTable?[] storageShards)
    {
        // Own copies of the arrays (the store mutates its arrays on fold) and pin every referenced file.
        _accountShards = (ShardTable?[])accountShards.Clone();
        _storageShards = (ShardTable?[])storageShards.Clone();
        // Safe without a Try variant: views are only built while the store holds its own live lease on
        // every shard it references.
        foreach (ShardTable? shard in _accountShards) Acquire(shard);
        foreach (ShardTable? shard in _storageShards) Acquire(shard);

        static void Acquire(ShardTable? shard)
        {
            shard?.File.AcquireLease();
            shard?.Filter?.AcquireLease();
        }
    }

    internal new bool TryAcquireLease() => base.TryAcquireLease();

    protected override void CleanUp()
    {
        foreach (ShardTable? shard in _accountShards) Release(shard);
        foreach (ShardTable? shard in _storageShards) Release(shard);

        static void Release(ShardTable? shard)
        {
            shard?.File.Dispose();
            shard?.Filter?.Dispose();
        }
    }

    /// <summary>Shard of <paramref name="key"/>: the top <c>log2(shardCount)</c> bits of its 16-bit
    /// prefix, so shards cover contiguous, ascending key ranges. <paramref name="shardCount"/> must be a
    /// power of two in [1, 65536]; every base-table key (20-byte account, 52-byte storage) has ≥ 2 bytes.</summary>
    internal static int ShardOf(ReadOnlySpan<byte> key, int shardCount) =>
        ((key[0] << 8) | key[1]) >> (16 - BitOperations.Log2((uint)shardCount));

    internal unsafe int GetAccount(ReadOnlySpan<byte> key20, Span<byte> outBuffer)
    {
        ShardTable? shard = _accountShards[ShardOf(key20, _accountShards.Length)];
        return shard is null ? 0 : Get(shard, key20, outBuffer);
    }

    internal unsafe bool TryGetStorage(ReadOnlySpan<byte> key52, Span<byte> outBuffer, out int size)
    {
        ShardTable? shard = _storageShards[ShardOf(key52, _storageShards.Length)];
        if (shard is null)
        {
            size = 0;
            return false;
        }

        size = Get(shard, key52, outBuffer);
        return size != 0;
    }

    private static unsafe int Get(ShardTable shard, ReadOnlySpan<byte> key, Span<byte> outBuffer)
    {
        // Misses are the descent this format cannot shorten any other way: without a filter they pay the
        // index-block search plus a data-block page fault only to find nothing.
        if (shard.Filter is not null && !shard.Filter.Filter.MightContain(BaseTableFilter.KeyHash(key))) return 0;

        MmapByteReader reader = new(shard.File.BasePtr, shard.Length);
        if (!SortedTableReader.TrySeek<MmapByteReader, NoOpPin>(in reader, new Bound(0, shard.Length), key, out Bound value))
            return 0;
        if (value.Length > outBuffer.Length || !reader.TryRead(value.Offset, outBuffer[..(int)value.Length]))
            ThrowCorruptValue(value.Length);
        return (int)value.Length;
    }

    /// <summary>Ascending cursor over the account shard tables restricted to
    /// <c>[startInclusive, endExclusive)</c>.</summary>
    internal BaseShardCursor CreateAccountCursor(byte[] startInclusive, byte[] endExclusive) =>
        new(_accountShards, startInclusive, endExclusive);

    /// <inheritdoc cref="CreateAccountCursor"/>
    internal BaseShardCursor CreateStorageCursor(byte[] startInclusive, byte[] endExclusive) =>
        new(_storageShards, startInclusive, endExclusive);

    [DoesNotReturn]
    private static void ThrowCorruptValue(long length) =>
        throw new InvalidOperationException(
            $"Corrupt flat base shard table: a record declares a {length}-byte value, which exceeds the maximum a base row can hold.");
}
