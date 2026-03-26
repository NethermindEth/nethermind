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
    private const int InitialCapacity = 4096 * 8;
    private static int LockPartitions => CollectionExtensions.LockPartitions;

    private long _committedBlockNumber = -1;
    private volatile Hash256? _committedBlockHash;
    private volatile bool _hasLegacyStorageClear;

    // Pending carry-forward writes buffered during FlushToTree, flushed after prewarm completion.
    // State: single-threaded producer (StateProvider.FlushToTree).
    // Storage: multi-threaded producers (PersistentStorageProvider.FlushToTree parallelizes across contracts).
    private readonly List<(AddressAsKey Key, Account? Account)> _pendingStateWrites = new();
    private readonly ConcurrentQueue<(StorageCell Cell, byte[] Value)> _pendingStorageWrites = new();

    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new();
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();
    private readonly SeqlockCache<NodeKey, byte[]?> _rlpCache = new();
    private readonly ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> _precompileCache = new(LockPartitions, InitialCapacity);

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;
    public SeqlockCache<NodeKey, byte[]?> RlpCache => _rlpCache;
    public ConcurrentDictionary<PrecompileCacheKey, Result<byte[]>> PrecompileCache => _precompileCache;

    public void ClearCaches()
    {
        _precompileCache.NoResizeClear();
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

    public void NoteStorageClear() => _hasLegacyStorageClear = true;

    public bool HasLegacyStorageClear => _hasLegacyStorageClear;

    public void ResetBlockFlags()
    {
        _hasLegacyStorageClear = false;
    }

    /// <summary>
    /// Buffer a state write for deferred carry-forward. Applied after prewarm completion.
    /// </summary>
    public void EnqueueStateWrite(AddressAsKey key, Account? account)
        => _pendingStateWrites.Add((key, account));

    /// <summary>
    /// Buffer a storage write for deferred carry-forward. Thread-safe (called from parallel FlushToTree).
    /// </summary>
    public void EnqueueStorageWrite(in StorageCell cell, byte[] value)
        => _pendingStorageWrites.Enqueue((cell, value));

    /// <summary>
    /// Flush all buffered carry-forward writes into the SeqlockCaches.
    /// Called from BranchProcessor after the prewarm task is complete and state is reset.
    /// </summary>
    public void FlushCarryForwardWrites()
    {
        foreach ((AddressAsKey key, Account? account) in _pendingStateWrites)
        {
            _stateCache.Set(key, account);
        }
        _pendingStateWrites.Clear();

        while (_pendingStorageWrites.TryDequeue(out (StorageCell Cell, byte[] Value) entry))
        {
            _storageCache.Set(entry.Cell, entry.Value);
        }
    }

    private void DiscardPendingCarryForward()
    {
        _pendingStateWrites.Clear();
        while (_pendingStorageWrites.TryDequeue(out _)) { }
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
