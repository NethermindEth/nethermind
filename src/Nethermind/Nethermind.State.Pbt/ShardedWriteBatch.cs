// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>Accumulates canonical leaf mutations for one key-zone partition between root folds.</summary>
/// <remarks>Writes and reads synchronize per shard. Enumeration, preparation and reset require joined writers.</remarks>
public sealed class ShardedWriteBatch : IDisposable, IResettable
{
    // Full inline keys and values make dictionary entries substantially larger than stem-map entries.
    private const int RetainedShardEntries = 512;
    private const int ShardCount = 16;
    private readonly Shard[] _shards = CreateShards();

    private sealed class Shard
    {
        internal readonly Lock Lock = new();
        internal Dictionary<PbtFullKey, ValueHash256?>? Entries;
    }

    private static Shard[] CreateShards()
    {
        Shard[] shards = new Shard[ShardCount];
        for (int index = 0; index < shards.Length; index++) shards[index] = new();
        return shards;
    }

    private static int ShardOf(PbtFullKey key)
    {
        if (PbtWriteBatchSet.PartitionOf(key) < 0)
            throw new ArgumentException("A canonical account, code or storage key is required.", nameof(key));
        return key.Bytes[1] >> 4;
    }

    internal void SetLeaf(PbtFullKey key, ValueHash256? value)
    {
        Shard shard = _shards[ShardOf(key)];
        lock (shard.Lock) (shard.Entries ??= [])[key] = value is null || value.Value == default ? null : value;
    }

    internal bool TryGetLeaf(PbtFullKey key, out ValueHash256? value)
    {
        Shard shard = _shards[ShardOf(key)];
        lock (shard.Lock)
        {
            value = null;
            return shard.Entries is not null && shard.Entries.TryGetValue(key, out value);
        }
    }

    internal int Count
    {
        get
        {
            int count = 0;
            foreach (Shard shard in _shards) count += shard.Entries?.Count ?? 0;
            return count;
        }
    }

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256?>> Leaves
    {
        get
        {
            foreach (Shard shard in _shards)
            {
                if (shard.Entries is null) continue;
                foreach (KeyValuePair<PbtFullKey, ValueHash256?> entry in shard.Entries) yield return entry;
            }
        }
    }

    /// <summary>Prepares transient canonical mutations and first-nibble counts at complete-key depth eight.</summary>
    /// <remarks>After a successful fold, call <see cref="CompleteDrain"/>. The updater mutates its operation array.</remarks>
    internal PbtPartitionWriteBatch PrepareDrain()
    {
        Span<int> deleteCounts = stackalloc int[ShardCount];
        deleteCounts.Clear();
        int[] table = new int[33];
        int compactCount = 0;
        int count = 0;
        for (int shardIndex = 0; shardIndex < _shards.Length; shardIndex++)
        {
            if (_shards[shardIndex].Entries is not { Count: > 0 } entries) continue;
            foreach (ValueHash256? value in entries.Values)
                if (value is null) deleteCounts[shardIndex]++;
            table[0] |= 1 << shardIndex;
            table[1 + compactCount++] = entries.Count;
            count += entries.Count;
        }

        PbtWriteOperation[] operations = new PbtWriteOperation[count];
        int offset = 0;
        for (int shardIndex = 0; shardIndex < _shards.Length; shardIndex++)
        {
            if (_shards[shardIndex].Entries is not { } entries) continue;
            int deleteOffset = offset;
            int setOffset = offset + deleteCounts[shardIndex];
            foreach ((PbtFullKey key, ValueHash256? value) in entries)
            {
                if (value is null) operations[deleteOffset++] = PbtWriteOperation.Delete(key);
                else operations[setOffset++] = PbtWriteOperation.Set(key, value.Value);
            }
            offset += entries.Count;
        }
        return new PbtPartitionWriteBatch(operations, table);
    }

    internal void CompleteDrain() => Reset();

    /// <summary>Discards pending mutations while retaining bounded shard capacity for reuse.</summary>
    public void Reset()
    {
        foreach (Shard shard in _shards)
        {
            if (shard.Entries is not { } entries) continue;
            if (entries.Count > RetainedShardEntries) shard.Entries = null;
            else entries.Clear();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Reset();
}
