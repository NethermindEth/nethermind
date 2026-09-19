// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.Pbt;

/// <summary>Accumulates whole-run mutations, keyed by run key, in shards selected by a key nibble.</summary>
/// <remarks>
/// Every entry is the whole run of the leaves under its run key (see <see cref="SlotRun.RunKey{TKey}"/>): folding
/// it replaces all of them, so a caller writing leaves one at a time must write every leaf of each run it touches
/// before building. Writes synchronize per shard. Count, enumeration, preparation and reset require joined writers.
/// </remarks>
/// <param name="shardNibbleIndex">The zero-based key nibble used to select a shard.</param>
public sealed class PbtWriteBatchBuilder<TKey>(int shardNibbleIndex) : IDisposable, IResettable where TKey : struct, IPbtKey<TKey>
{
    // Full inline keys make dictionary entries substantially larger than stem-map entries.
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
        // The shard owns the runs it holds.
        internal Dictionary<TKey, ISlotRun>? Entries;
    }

    private sealed class ShardPoolPolicy : IPooledObjectPolicy<Shard>
    {
        public Shard Create() => new();

        public bool Return(Shard shard)
        {
            if (shard.Entries is { } entries)
            {
                foreach ((_, ISlotRun run) in entries) SlotRun.Return(run);
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

    /// <summary>Sets one leaf of its run, or clears it when the value is zero.</summary>
    public void Set(TKey key, in ValueHash256 value) => SetLeaf(key, value);

    /// <summary>Clears one leaf of its run.</summary>
    public void Delete(TKey key) => SetLeaf(key, null);

    internal void SetLeaf(TKey key, ValueHash256? value)
    {
        EvmWord word = value is null ? default : EvmWordSlot.FromStripped(value.Value.Bytes);
        Shard shard = TakeShard(key);
        lock (shard.Lock)
        {
            Dictionary<TKey, ISlotRun> entries = shard.Entries ??= [];
            TKey runKey = SlotRun.RunKey(key);
            ISlotRun previous = entries.TryGetValue(runKey, out ISlotRun? held) ? held : SlotRun.Empty;
            entries[runKey] = previous.With(SlotRun.IndexOf(key), word);
            SlotRun.Return(previous);
        }
    }

    /// <summary>Sets the whole run keyed by <paramref name="runKey"/>, copying <paramref name="run"/>.</summary>
    public void SetRun(TKey runKey, ISlotRun run)
    {
        if (!SlotRun.IsRunKey(runKey)) throw new ArgumentException("A run key has its low four bits cleared.", nameof(runKey));
        ISlotRun copy = run.Clone();
        Shard shard = TakeShard(runKey);
        lock (shard.Lock)
        {
            Dictionary<TKey, ISlotRun> entries = shard.Entries ??= [];
            entries.TryGetValue(runKey, out ISlotRun? previous);
            entries[runKey] = copy;
            if (previous is not null) SlotRun.Return(previous);
        }
    }

    private Shard TakeShard(TKey key)
    {
        ref Shard? shardSlot = ref _shards[ShardOf(key)];
        Shard? shard = Volatile.Read(ref shardSlot);
        if (shard is not null) return shard;
        Shard rented = ShardPool.Get();
        shard = Interlocked.CompareExchange(ref shardSlot, rented, null);
        if (shard is null) return rented;
        ShardPool.Return(rented);
        return shard;
    }

    /// <summary>Gets the number of pending runs.</summary>
    public int Count
    {
        get
        {
            int count = 0;
            foreach (Shard? shard in _shards) count += shard?.Entries?.Count ?? 0;
            return count;
        }
    }

    /// <summary>The pending runs, borrowed.</summary>
    internal IEnumerable<PbtWriteOperation<TKey>> Operations
    {
        get
        {
            for (int shardIndex = 0; shardIndex < ShardCount; shardIndex++)
            {
                if (_shards[shardIndex]?.Entries is not { } entries) continue;
                foreach ((TKey runKey, ISlotRun run) in entries) yield return new(runKey, run);
            }
        }
    }

    /// <summary>The pending leaves: every slot of every run, a null value for a cleared slot.</summary>
    internal IEnumerable<KeyValuePair<TKey, ValueHash256?>> Leaves
    {
        get
        {
            foreach ((TKey runKey, ISlotRun run) in Operations)
                for (int index = 0; index < SlotRun.Width; index++)
                {
                    // A bare null would take ValueHash256's implicit conversion and become a zero hash.
                    ValueHash256? value = (run.Mask & (1 << index)) == 0 ? (ValueHash256?)null : SlotRun.LeafValue(run, index);
                    yield return new(SlotRun.SlotKey(runKey, index), value);
                }
        }
    }

    /// <summary>Builds an independent, single-use batch, owning copies of the runs, without clearing pending mutations.</summary>
    /// <remarks>Writers must be joined before building. Dispose the batch if it is not consumed by the updater.
    /// Reset only after a successful fold to retain mutations for retry.</remarks>
    public PbtWriteBatch<TKey> Build()
    {
        ArrayPoolList<int> table = new(ShardCount + 1, ShardCount + 1);
        ArrayPoolList<PbtWriteOperation<TKey>>? operations = null;
        try
        {
            int compactCount = 0;
            int count = 0;
            for (int shardIndex = 0; shardIndex < ShardCount; shardIndex++)
            {
                if (_shards[shardIndex]?.Entries is not { Count: > 0 } entries) continue;
                table[0] |= 1 << shardIndex;
                table[1 + compactCount++] = entries.Count;
                count += entries.Count;
            }

            operations = new(count, count);
            int offset = 0;
            foreach ((TKey runKey, ISlotRun run) in Operations) operations[offset++] = new(runKey, run.Clone());
            return new PbtWriteBatch<TKey>(operations, table, _shardNibbleIndex);
        }
        catch
        {
            if (operations is not null) PbtWriteBatch<TKey>.ReturnRuns(operations.AsSpan());
            operations?.Dispose();
            table.Dispose();
            throw;
        }
    }

    internal void CompleteDrain() => Reset();

    /// <summary>Discards pending mutations, returning their runs to their pools and shards to the pool.</summary>
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
