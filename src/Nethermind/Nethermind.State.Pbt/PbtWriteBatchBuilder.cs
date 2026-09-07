// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.State.Pbt;

/// <summary>Accumulates canonical leaf mutations for one writable branch between root folds.</summary>
/// <remarks>Writes, reads and overlay copying synchronize per shard. Enumeration, preparation and reset require joined writers.</remarks>
public sealed class PbtWriteBatchBuilder : IDisposable, IResettable
{
    // Full inline keys and values make dictionary entries substantially larger than stem-map entries.
    private const int RetainedShardEntries = 512;
    private readonly Shard[] _shards = CreateShards();

    private sealed class Shard
    {
        internal readonly Lock Lock = new();
        internal Dictionary<PbtFullKey, ValueHash256?>? Entries;
    }

    private static Shard[] CreateShards()
    {
        Shard[] shards = new Shard[3 * 256];
        for (int index = 0; index < shards.Length; index++) shards[index] = new();
        return shards;
    }

    private static int ShardOf(PbtFullKey key)
    {
        int partition = PbtWriteBatchSet.PartitionOf(key);
        if (partition < 0) throw new ArgumentException("A canonical account, code or storage key is required.", nameof(key));
        return partition * 256 + key.Bytes[1];
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

    /// <summary>Prepares transient canonical mutations for the next root fold.</summary>
    /// <remarks>After a successful fold, call <see cref="CompleteDrain"/>. The updater mutates its operation array.</remarks>
    internal PbtWriteBatchSet PrepareDrain()
    {
        Span<int> counts = stackalloc int[3 * 256 * 2];
        counts.Clear();
        int count = 0;
        for (int shardIndex = 0; shardIndex < _shards.Length; shardIndex++)
        {
            if (_shards[shardIndex].Entries is not { } entries) continue;
            foreach (ValueHash256? value in entries.Values) counts[shardIndex * 2 + (value is null ? 0 : 1)]++;
            count += entries.Count;
        }

        PbtWriteOperation[] operations = new PbtWriteOperation[count];
        int offset = 0;
        for (int shardIndex = 0; shardIndex < _shards.Length; shardIndex++)
        {
            if (_shards[shardIndex].Entries is not { } entries) continue;
            int deleteOffset = offset;
            int setOffset = offset + counts[shardIndex * 2];
            foreach ((PbtFullKey key, ValueHash256? value) in entries)
            {
                if (value is null) operations[deleteOffset++] = PbtWriteOperation.Delete(key);
                else operations[setOffset++] = PbtWriteOperation.Set(key, value.Value);
            }
            offset += entries.Count;
        }
        return PbtWriteBatchSet.CreateGrouped(operations, counts);
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
