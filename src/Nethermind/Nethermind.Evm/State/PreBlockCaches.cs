// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private readonly record struct PendingStorageWrite(StorageCell Cell, byte[] Value, int ClearVersion);

    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private long _committedBlockNumber = -1;
    private volatile Hash256? _committedBlockHash;
    private volatile bool _hasLegacyStorageClear;
    private readonly ConcurrentDictionary<AddressAsKey, int> _storageClearVersions = new(LockPartitions, InitialCapacity);

    // Pending carry-forward writes buffered during FlushToTree, flushed after prewarm completion.
    // State: single-threaded producer (StateProvider.FlushToTree).
    // Storage: multi-threaded producers (PersistentStorageProvider.FlushToTree parallelizes across contracts).
    private readonly List<(AddressAsKey Key, Account? Account)> _pendingStateWrites = new();
    private readonly ConcurrentQueue<PendingStorageWrite> _pendingStorageWrites = new();

    private readonly ShardedSeqlockCache<StorageCell, byte[]> _storageCache = new(8);
    private readonly ShardedSeqlockCache<AddressAsKey, Account> _stateCache = new(4);
    private readonly SeqlockCache<NodeKey, byte[]?> _rlpCache = new();
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);

    public ShardedSeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public ShardedSeqlockCache<AddressAsKey, Account> StateCache => _stateCache;
    public SeqlockCache<NodeKey, byte[]?> RlpCache => _rlpCache;
    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache => _precompileCache;

    public CacheType ClearCaches()
    {
        // State/storage caches carry committed writes into the next block and are invalidated separately.
        _precompileCache.NoResizeClear();
        return CacheType.None;
    }

    public void InvalidateCaches()
    {
        _storageCache.Clear();
        _stateCache.Clear();
        _committedBlockHash = null;
        Volatile.Write(ref _committedBlockNumber, -1);
        ResetBlockFlags();
        DiscardPendingCarryForward();
    }

    public void RecordCommittedBlock(long blockNumber, Hash256? blockHash)
    {
        _committedBlockHash = blockHash;
        Volatile.Write(ref _committedBlockNumber, blockNumber);
    }

    public bool IsValidForParent(long parentNumber, Hash256? parentHash)
        => Volatile.Read(ref _committedBlockNumber) == parentNumber
            && _committedBlockHash == parentHash;

    public void NoteStorageClear(Address address)
    {
        _hasLegacyStorageClear = true;
        _storageClearVersions.AddOrUpdate(address, 1, static (_, version) => version + 1);
    }

    public bool HasLegacyStorageClear => _hasLegacyStorageClear;

    public void ResetBlockFlags()
    {
        _hasLegacyStorageClear = false;
        _storageClearVersions.Clear();
    }

    /// <summary>
    /// Buffer a state write for deferred carry-forward. Applied after prewarm completion.
    /// </summary>
    public void EnqueueStateWrite(AddressAsKey key, Account? account) => _pendingStateWrites.Add((key, account));

    /// <summary>
    /// Buffer a storage write for deferred carry-forward. Thread-safe (called from parallel FlushToTree).
    /// </summary>
    public void EnqueueStorageWrite(in StorageCell cell, byte[] value)
    {
        int clearVersion = 0;
        if (_hasLegacyStorageClear)
        {
            clearVersion = GetStorageClearVersion(cell.Address);
        }
        _pendingStorageWrites.Enqueue(new PendingStorageWrite(cell, value, clearVersion));
    }

    /// <summary>
    /// Flush buffered carry-forward writes into the SeqlockCaches.
    /// State cache is rebuilt from scratch (accounts are few, correctness is critical).
    /// Storage cache preserves prewarmed reads and overlays writes (main perf benefit).
    /// Called from BranchProcessor after the prewarm task is complete and state is reset.
    /// </summary>
    public void FlushCarryForwardWrites()
    {
        _stateCache.Clear();

        foreach ((AddressAsKey key, Account? account) in _pendingStateWrites)
        {
            _stateCache.Set(key, account);
        }
        _pendingStateWrites.Clear();

        if (_hasLegacyStorageClear)
        {
            _storageCache.Clear();
            while (_pendingStorageWrites.TryDequeue(out PendingStorageWrite entry))
            {
                if (entry.ClearVersion == GetStorageClearVersion(entry.Cell.Address))
                {
                    _storageCache.Set(entry.Cell, entry.Value);
                }
            }
        }
        else
        {
            while (_pendingStorageWrites.TryDequeue(out PendingStorageWrite entry))
            {
                _storageCache.Set(entry.Cell, entry.Value);
            }
        }

        ResetBlockFlags();
    }

    private void DiscardPendingCarryForward()
    {
        _pendingStateWrites.Clear();
        while (_pendingStorageWrites.TryDequeue(out _)) { }
        _storageClearVersions.Clear();
    }

    private int GetStorageClearVersion(AddressAsKey address)
        => _storageClearVersions.TryGetValue(address, out int version) ? version : 0;

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
