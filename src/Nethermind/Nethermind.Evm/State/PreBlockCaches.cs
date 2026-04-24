// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Evm.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private long _committedBlockNumber = -1;
    private volatile Hash256? _committedBlockHash;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new();
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private readonly SeqlockCache<NodeKey, byte[]?> _rlpCache = new();
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;
    public SeqlockCache<NodeKey, byte[]?> RlpCache => _rlpCache;
    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache => _precompileCache;

    public CacheType ClearCaches()
    {
        _precompileCache.NoResizeClear();
        return CacheType.None;
    }

    public void InvalidateCaches()
    {
        _storageCache.Clear();
        _stateCache.Clear();
        _committedBlockHash = null;
        Volatile.Write(ref _committedBlockNumber, -1);
    }

    public void RecordCommittedBlock(long blockNumber, Hash256? blockHash)
    {
        _committedBlockHash = blockHash;
        Volatile.Write(ref _committedBlockNumber, blockNumber);
    }

    public bool IsValidForParent(long parentNumber, Hash256? parentHash)
        => Volatile.Read(ref _committedBlockNumber) == parentNumber
            && _committedBlockHash == parentHash;

    /// <summary>
    /// Write a state entry directly into the cache. Thread-safe via SeqlockCache.
    /// </summary>
    public void SetState(AddressAsKey key, Account? account) => _stateCache.Set(key, account);

    /// <summary>
    /// Write a storage entry directly into the cache. Thread-safe via SeqlockCache.
    /// </summary>
    public void SetStorage(in StorageCell cell, byte[] value) => _storageCache.Set(cell, value);

    /// <summary>
    /// Invalidate all cached storage slots for correctness (SELFDESTRUCT/CREATE2).
    /// </summary>
    public void NoteStorageClear() => _storageCache.Clear();

    /// <summary>
    /// Finalize carry-forward after prewarm completes.
    /// State cache is rebuilt from the write set; storage cache retains prewarmed reads.
    /// </summary>
    public void FlushCarryForwardWrites()
    {
        // No-op: writes were applied directly during processing.
        // State cache already has post-block values from CachePopulatingWriteBatch.
        // Storage cache already has post-block values overlaid on prewarmed reads.
    }

    public readonly struct PrecompileCacheKey(Address address, ReadOnlyMemory<byte> data) : IEquatable<PrecompileCacheKey>
    {
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        public bool Equals(PrecompileCacheKey other) => Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
        public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode();
    }
}

[Flags]
public enum CacheType
{
    None = 0,
    Storage = 0b1,
    State = 0b10,
    Rlp = 0b100,
    Precompile = 0b1000
}
