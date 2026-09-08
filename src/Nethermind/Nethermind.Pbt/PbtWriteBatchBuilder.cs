// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.Pbt;

/// <summary>Accumulates complete-key mutations in shards selected by a key nibble.</summary>
/// <remarks>Writes synchronize per shard. Count, enumeration, preparation and reset require joined writers.</remarks>
/// <param name="shardNibbleIndex">The zero-based key nibble used to select a shard.</param>
public sealed class PbtWriteBatchBuilder<TKey>(int shardNibbleIndex) : IDisposable, IResettable where TKey : struct, IPbtKey<TKey>
{
    // Full inline keys and values make dictionary entries substantially larger than stem-map entries.
    private const int RetainedShardEntries = 512;
    private const int ShardCount = 16;
    private static readonly ObjectPool<Shard> ShardPool = new DefaultObjectPool<Shard>(new ShardPoolPolicy(), ShardCount * 2);
    private ShardBuffer _shards;

    private readonly int _shardNibbleIndex = ValidateShardNibbleIndex(shardNibbleIndex);

    private static int ValidateShardNibbleIndex(int shardNibbleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)shardNibbleIndex, (uint)(TKey.Capacity * 2));
        return shardNibbleIndex;
    }

    private sealed class Shard
    {
        internal readonly Lock Lock = new();
        internal Dictionary<TKey, ValueHash256?>? Entries;
    }

    private sealed class ShardPoolPolicy : IPooledObjectPolicy<Shard>
    {
        public Shard Create() => new();

        public bool Return(Shard shard)
        {
            if (shard.Entries is { } entries)
            {
                if (entries.Count > RetainedShardEntries) shard.Entries = null;
                else entries.Clear();
            }
            return true;
        }
    }

    [InlineArray(ShardCount)]
    private struct ShardBuffer
    {
        private Shard? _element;
    }

    private int ShardOf(TKey key)
    {
        if (key.Length * 2 <= _shardNibbleIndex)
            throw new ArgumentException("The complete key must contain the sharding nibble.", nameof(key));
        return (key.Bytes[_shardNibbleIndex >> 1] >> ((_shardNibbleIndex & 1) == 0 ? 4 : 0)) & 15;
    }

    /// <summary>Adds an explicit complete-key value mutation.</summary>
    public void Set(TKey key, in ValueHash256 value) => SetMutation(key, value);

    /// <summary>Adds an explicit complete-key deletion.</summary>
    public void Delete(TKey key) => SetMutation(key, null);

    internal void SetLeaf(TKey key, ValueHash256? value) =>
        SetMutation(key, value is null || value.Value == default ? null : value);

    private void SetMutation(TKey key, ValueHash256? value)
    {
        ref Shard? shardSlot = ref _shards[ShardOf(key)];
        Shard? shard = Volatile.Read(ref shardSlot);
        if (shard is null)
        {
            Shard rented = ShardPool.Get();
            shard = Interlocked.CompareExchange(ref shardSlot, rented, null);
            if (shard is null) shard = rented;
            else ShardPool.Return(rented);
        }
        lock (shard.Lock) (shard.Entries ??= [])[key] = value;
    }

    /// <summary>Gets the number of pending unique mutations.</summary>
    public int Count
    {
        get
        {
            int count = 0;
            foreach (Shard? shard in _shards) count += shard?.Entries?.Count ?? 0;
            return count;
        }
    }

    internal IEnumerable<KeyValuePair<TKey, ValueHash256?>> Leaves
    {
        get
        {
            for (int shardIndex = 0; shardIndex < ShardCount; shardIndex++)
            {
                if (_shards[shardIndex]?.Entries is not { } entries) continue;
                foreach (KeyValuePair<TKey, ValueHash256?> entry in entries) yield return entry;
            }
        }
    }

    internal IEnumerable<PbtWriteOperation<TKey>> Operations
    {
        get
        {
            foreach ((TKey key, ValueHash256? value) in Leaves)
                if (value is null) yield return PbtWriteOperation<TKey>.Delete(key);
            foreach ((TKey key, ValueHash256? value) in Leaves)
                if (value is { } hash) yield return PbtWriteOperation<TKey>.Set(key, hash);
        }
    }

    /// <summary>Builds an independent, single-use batch without clearing pending mutations.</summary>
    /// <remarks>Writers must be joined before building. Dispose the batch if it is not consumed by the updater.
    /// Reset only after a successful fold to retain mutations for retry.</remarks>
    public PbtWriteBatch<TKey> Build()
    {
        Span<int> deleteCounts = stackalloc int[ShardCount];
        deleteCounts.Clear();
        ArrayPoolList<int> table = new(33, 33);
        ArrayPoolList<PbtWriteOperation<TKey>>? operations = null;
        try
        {
            int compactCount = 0;
            int count = 0;
            for (int shardIndex = 0; shardIndex < ShardCount; shardIndex++)
            {
                if (_shards[shardIndex]?.Entries is not { Count: > 0 } entries) continue;
                foreach (ValueHash256? value in entries.Values)
                    if (value is null) deleteCounts[shardIndex]++;
                table[0] |= 1 << shardIndex;
                table[1 + compactCount++] = entries.Count;
                count += entries.Count;
            }

            operations = new(count, count);
            int offset = 0;
            for (int shardIndex = 0; shardIndex < ShardCount; shardIndex++)
            {
                if (_shards[shardIndex]?.Entries is not { } entries) continue;
                int deleteOffset = offset;
                int setOffset = offset + deleteCounts[shardIndex];
                foreach ((TKey key, ValueHash256? value) in entries)
                {
                    if (value is null) operations[deleteOffset++] = PbtWriteOperation<TKey>.Delete(key);
                    else operations[setOffset++] = PbtWriteOperation<TKey>.Set(key, value.Value);
                }
                offset += entries.Count;
            }
            return new PbtWriteBatch<TKey>(operations, table, _shardNibbleIndex);
        }
        catch
        {
            operations?.Dispose();
            table.Dispose();
            throw;
        }
    }

    internal void CompleteDrain() => Reset();

    /// <summary>Discards pending mutations and returns shards to the pool.</summary>
    public void Reset()
    {
        for (int shardIndex = 0; shardIndex < ShardCount; shardIndex++)
        {
            if (_shards[shardIndex] is not { } shard) continue;
            _shards[shardIndex] = null;
            ShardPool.Return(shard);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Reset();
}
