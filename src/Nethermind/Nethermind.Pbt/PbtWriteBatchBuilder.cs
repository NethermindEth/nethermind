// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using IResettable = Nethermind.Core.Resettables.IResettable;

namespace Nethermind.Pbt;

/// <summary>Accumulates complete-key mutations in shards selected by a key nibble.</summary>
/// <remarks>Writes and reads synchronize per shard. Count, enumeration, preparation and reset require joined writers.</remarks>
/// <param name="shardNibbleIndex">The zero-based key nibble used to select a shard.</param>
public sealed class PbtWriteBatchBuilder(int shardNibbleIndex) : IDisposable, IResettable
{
    // Full inline keys and values make dictionary entries substantially larger than stem-map entries.
    private const int RetainedShardEntries = 512;
    private const int ShardCount = 16;
    private readonly Shard[] _shards = CreateShards();

    private readonly int _shardNibbleIndex = ValidateShardNibbleIndex(shardNibbleIndex);

    private static int ValidateShardNibbleIndex(int shardNibbleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)shardNibbleIndex, (uint)(PbtFullKey.MaxLength * 2));
        return shardNibbleIndex;
    }

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

    private int ShardOf(PbtFullKey key)
    {
        if (key.Length * 2 <= _shardNibbleIndex)
            throw new ArgumentException("The complete key must contain the sharding nibble.", nameof(key));
        return (key.Bytes[_shardNibbleIndex >> 1] >> ((_shardNibbleIndex & 1) == 0 ? 4 : 0)) & 15;
    }

    /// <summary>Adds an explicit complete-key value mutation.</summary>
    public void Set(PbtFullKey key, in ValueHash256 value) => SetMutation(key, value);

    /// <summary>Adds an explicit complete-key deletion.</summary>
    public void Delete(PbtFullKey key) => SetMutation(key, null);

    internal void SetLeaf(PbtFullKey key, ValueHash256? value) =>
        SetMutation(key, value is null || value.Value == default ? null : value);

    private void SetMutation(PbtFullKey key, ValueHash256? value)
    {
        Shard shard = _shards[ShardOf(key)];
        lock (shard.Lock) (shard.Entries ??= [])[key] = value;
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

    /// <summary>Gets the number of pending unique mutations.</summary>
    public int Count
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

    internal IEnumerable<PbtWriteOperation> Operations
    {
        get
        {
            foreach ((PbtFullKey key, ValueHash256? value) in Leaves)
                if (value is null) yield return PbtWriteOperation.Delete(key);
            foreach ((PbtFullKey key, ValueHash256? value) in Leaves)
                if (value is { } hash) yield return PbtWriteOperation.Set(key, hash);
        }
    }

    /// <summary>Builds an independent, single-use batch without clearing pending mutations.</summary>
    /// <remarks>Writers must be joined before building. Reset only after a successful fold to retain mutations for retry.</remarks>
    public PbtWriteBatch Build()
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
        return new PbtWriteBatch(operations, table, _shardNibbleIndex);
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
