// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Collections;
using Nethermind.Trie;

using CollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.State;

public class PreBlockCaches
{
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new();
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private readonly SeqlockCache<NodeKey, byte[]?> _rlpCache = new();
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);

    /// <summary>
    /// Hash of the last successfully processed block whose deltas were applied to the cache.
    /// Used for fork detection: if the next block's parent hash doesn't match, the cache is stale.
    /// </summary>
    public Hash256? LastProcessedBlockHash { get; set; }

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;
    public SeqlockCache<NodeKey, byte[]?> RlpCache => _rlpCache;
    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache => _precompileCache;

    /// <summary>
    /// Clears only per-block caches (precompile). State and storage caches are kept warm
    /// across blocks and updated with block deltas after each commit.
    /// </summary>
    public CacheType ClearCaches()
    {
        // DEBUG: keep state warm, clear storage per block to isolate issue
        _storageCache.Clear();
        _precompileCache.NoResizeClear();
        return CacheType.None;
    }

    /// <summary>
    /// Clears all caches including warm state/storage caches.
    /// Used on fork/reorg detection or error recovery.
    /// </summary>
    public CacheType ClearAllCaches()
    {
        _storageCache.Clear();
        _stateCache.Clear();
        _precompileCache.NoResizeClear();
        LastProcessedBlockHash = null;
        return CacheType.None;
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
