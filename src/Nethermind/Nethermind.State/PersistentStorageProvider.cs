// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State;

using Nethermind.Core.Cpu;

/// <summary>
/// Manages persistent storage allowing for snapshotting and restoring
/// Persists data to ITrieStore
/// </summary>
internal sealed class PersistentStorageProvider : PartialStorageProviderBase
{
    private IWorldStateScopeProvider.IScope _currentScope;
    private readonly StateProvider _stateProvider;
    private readonly Dictionary<AddressAsKey, PerContractState> _storages = new(4_096);
    private readonly Dictionary<AddressAsKey, bool> _toUpdateRoots = new();

    /// <summary>
    /// EIP-1283
    /// </summary>
    private readonly Dictionary<StorageCell, byte[]> _originalValues = new();

    /// <summary>
    /// Manages persistent storage allowing for snapshotting and restoring
    /// Persists data to ITrieStore
    /// </summary>
    public PersistentStorageProvider(
        StateProvider stateProvider,
        ILogManager logManager) : base(logManager)
    {
        _stateProvider = stateProvider;
    }

    /// <summary>
    /// Reset the storage state
    /// </summary>
    public override void Reset(bool resetBlockChanges = true)
    {
        base.Reset();
        _originalValues.Clear();
        if (resetBlockChanges)
        {
            _storages.ResetAndClear();
            _toUpdateRoots.Clear();
        }
    }

    public void SetBackendScope(IWorldStateScopeProvider.IScope scope)
    {
        _currentScope = scope;
    }

    /// <summary>
    /// Get the current value at the specified location
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <returns>Value at location</returns>
    protected override ReadOnlySpan<byte> GetCurrentValue(in StorageCell storageCell)
    {
        if (!storageCell.IsHash && TryGetCachedValue(storageCell, out byte[]? bytes))
        {
            return bytes!;
        }

        byte[] value = LoadFromTree(storageCell);
        if (!storageCell.IsHash)
        {
            int slotIndex = GetOrCreateSlotIndex(storageCell);
            CacheReadSlot(slotIndex, value);
            _originalValues[storageCell] = value;
        }

        return value;
    }

    /// <summary>
    /// Return the original persistent storage value from the storage cell
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    public byte[] GetOriginal(in StorageCell storageCell)
    {
        if (!_originalValues.TryGetValue(storageCell, out byte[]? value))
        {
            ThrowNotAccessed();
        }

        if (!TryGetSlotIndex(storageCell, out int slotIndex))
        {
            return value;
        }

        if (!TryGetCurrentTransactionStartOffset(out int transactionStartOffset))
        {
            return value;
        }

        byte[]? valueAtTransactionStart = FindValueBeforeOffset(slotIndex, transactionStartOffset);
        if (valueAtTransactionStart is not null)
        {
            return valueAtTransactionStart;
        }

        // No undo entries found for this slot since the transaction start.
        // Two cases depending on when the slot was first touched:
        // - Prior tx frame (OwnerFrameId < currentFrameId): the slot was loaded/written
        //   before this tx. _slotsHot has the value at tx start (GetOriginal is called
        //   before Set, so current SSTORE hasn't modified it yet).
        // - Current tx frame (OwnerFrameId == currentFrameId): the slot was first accessed
        //   in this tx. No undo entries were created because GetOrCreateSlotIndex sets
        //   OwnerFrameId = currentFrameId. _originalValues has the correct tree value.
        if (_slotsMeta[slotIndex].OwnerFrameId < _currentFrameId)
        {
            return _slotsHot[slotIndex] ?? value;
        }

        return value;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowNotAccessed()
            => throw new InvalidOperationException("Get original should only be called after get within the same caching round");
    }

    public Hash256 GetStorageRoot(Address address)
    {
        return GetOrCreateStorage(address).StorageRoot;
    }

    public bool IsStorageEmpty(Address address) =>
        GetOrCreateStorage(address).IsEmpty;

    private HashSet<AddressAsKey>? _tempToUpdateRoots;
    /// <summary>
    /// Called by Commit
    /// Used for persistent storage specific logic
    /// </summary>
    /// <param name="tracer">Storage tracer</param>
    protected override void CommitCore(IStorageTracer tracer)
    {
        if (_logger.IsTrace) _logger.Trace("Committing storage changes");

        HashSet<AddressAsKey> toUpdateRoots = (_tempToUpdateRoots ??= new());

        bool isTracing = tracer.IsTracingStorage;
        Dictionary<StorageCell, StorageChangeTrace>? trace = null;
        if (isTracing)
        {
            trace = [];
        }

        bool isLogTracing = _logger.IsTrace;
        for (int i = 0; i < _dirtyCount; i++)
        {
            int slotIndex = _dirtySlotIndices[i];
            ref SlotMeta meta = ref _slotsMeta[slotIndex];
            if ((meta.Flags & SlotFlags.Dirty) == 0)
            {
                continue;
            }

            StorageCell storageCell = meta.Cell;
            byte[] value = _slotsHot[slotIndex]!;
            if (isLogTracing)
            {
                Trace(in storageCell, value);
            }

            if (_originalValues.TryGetValue(storageCell, out byte[]? initialValue) &&
                initialValue.AsSpan().SequenceEqual(value))
            {
                // no need to update the tree if the value is the same
            }
            else
            {
                toUpdateRoots.Add(storageCell.Address);
                GetOrCreateStorage(storageCell.Address).SaveChange(storageCell, value);
            }

            if (isTracing)
            {
                if (initialValue is not null)
                {
                    trace![storageCell] = new StorageChangeTrace(initialValue, value);
                }
                else
                {
                    trace![storageCell] = new StorageChangeTrace(value);
                }
            }
        }

        if (isTracing)
        {
            for (int i = 0; i < _slotCount; i++)
            {
                ref SlotMeta meta = ref _slotsMeta[i];
                if ((meta.Flags & (SlotFlags.Read | SlotFlags.Dirty)) == SlotFlags.Read)
                {
                    tracer.ReportStorageRead(meta.Cell);
                }
            }
        }

        foreach (AddressAsKey address in toUpdateRoots)
        {
            // since the accounts could be empty accounts that are removing (EIP-158)
            if (_stateProvider.AccountExists(address))
            {
                _toUpdateRoots[address] = true;
                // Add storage tree, will accessed later, which may be in parallel
                // As we can't add a new storage tries in parallel to the _storages Dict do it here
                GetOrCreateStorage(address).EnsureStorageTree();
            }
            else
            {
                _toUpdateRoots.Remove(address);
                if (_storages.TryGetValue(address, out PerContractState? storage))
                {
                    // BlockChange need to be kept to keep selfdestruct marker (via DefaultableDictionary) working.
                    storage.RemoveStorageTree();
                }
            }
        }
        toUpdateRoots.Clear();

        base.CommitCore(tracer);
        _originalValues.Clear();

        if (isTracing)
        {
            ReportChanges(tracer, trace!);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(in StorageCell storageCell, byte[] value)
            => _logger.Trace($"  Update {storageCell.Address}_{storageCell.Index} V = {value.ToHexString(true)}");
    }

    internal void FlushToTree(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        if (_toUpdateRoots.Count == 0)
        {
            return;
        }

        // Is overhead of parallel foreach worth it?
        if (_toUpdateRoots.Count < 3)
        {
            UpdateRootHashesSingleThread();
        }
        else
        {
            UpdateRootHashesMultiThread();
        }

        _toUpdateRoots.Clear();

        void UpdateRootHashesSingleThread()
        {
            foreach (KeyValuePair<AddressAsKey, PerContractState> kvp in _storages)
            {
                if (!_toUpdateRoots.TryGetValue(kvp.Key, out bool hasChanges) || !hasChanges)
                {
                    // Wasn't updated don't recalculate
                    continue;
                }

                PerContractState contractState = kvp.Value;
                (int writes, int skipped) = contractState.ProcessStorageChanges(writeBatch.CreateStorageWriteBatch(kvp.Key, kvp.Value.EstimatedChanges));
                ReportMetrics(writes, skipped);
            }
        }

        void UpdateRootHashesMultiThread()
        {
            // We can recalculate the roots in parallel as they are all independent tries
            using ArrayPoolList<(AddressAsKey Key, PerContractState ContractState, IWorldStateScopeProvider.IStorageWriteBatch WriteBatch)> storages = _storages
                // Only consider contracts that actually have pending changes
                .Where(kv => _toUpdateRoots.TryGetValue(kv.Key, out bool hasChanges) && hasChanges)
                // Schedule larger changes first to help balance the work
                .OrderByDescending(kv => kv.Value.EstimatedChanges)
                .Select((kv) => (
                    kv.Key,
                    kv.Value,
                    writeBatch.CreateStorageWriteBatch(kv.Key, kv.Value.EstimatedChanges)
                ))
                .ToPooledList(_storages.Count);

            ParallelUnbalancedWork.For(
                0,
                storages.Count,
                RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                (storages, toUpdateRoots: _toUpdateRoots, writes: 0, skips: 0),
                static (i, state) =>
                {
                    ref var kvp = ref state.storages.GetRef(i);
                    (int writes, int skipped) = kvp.ContractState.ProcessStorageChanges(kvp.WriteBatch);
                    if (writes == 0)
                    {
                        // Mark as no changes; we set as false rather than removing so
                        // as not to modify the non-concurrent collection without synchronization
                        state.toUpdateRoots[kvp.Key] = false;
                    }
                    else
                    {
                        state.writes += writes;
                    }

                    state.skips += skipped;

                    return state;
                },
                (state) => ReportMetrics(state.writes, state.skips));
        }

        static void ReportMetrics(int writes, int skipped)
        {
            if (skipped > 0)
            {
                Db.Metrics.IncrementStorageSkippedWrites(skipped);
            }
            if (writes > 0)
            {
                Db.Metrics.IncrementStorageTreeWrites(writes);
            }
        }
    }

    public void ClearStorageMap()
    {
        _storages.Clear();
    }

    private PerContractState GetOrCreateStorage(Address address)
    {
        ref PerContractState? value = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (!exists) value = PerContractState.Rent(address, this);
        return value;
    }

    public void WarmUp(in StorageCell storageCell, bool isEmpty)
    {
        if (isEmpty)
        {
        }
        else
        {
            GetCurrentValue(in storageCell);
        }
    }

    private bool TryGetCurrentTransactionStartOffset(out int offset)
    {
        while (TryGetTransactionSnapshot(out int snapshot))
        {
            if (TryGetUndoOffsetForSnapshot(snapshot, out offset))
            {
                return true;
            }

            // Transaction-start markers can outlive their frame ids after restores.
            // Prune stale markers lazily so GetOriginal keeps finding a valid boundary.
            _transactionStartSnapshots.Pop();
        }

        offset = 0;
        return false;
    }

    private byte[] LoadFromTree(in StorageCell storageCell)
    {
        return GetOrCreateStorage(storageCell.Address).LoadFromTree(storageCell);
    }

    private static void ReportChanges(IStorageTracer tracer, Dictionary<StorageCell, StorageChangeTrace> trace)
    {
        foreach ((StorageCell address, StorageChangeTrace change) in trace)
        {
            byte[] before = change.Before;
            byte[] after = change.After;

            if (!Bytes.AreEqual(before, after))
            {
                tracer.ReportStorageChange(address, before, after);
            }
        }
    }

    /// <summary>
    /// Clear all storage at specified address
    /// </summary>
    /// <param name="address">Contract address</param>
    public override void ClearStorage(Address address)
    {
        base.ClearStorage(address);

        _toUpdateRoots.TryAdd(address, true);

        PerContractState state = GetOrCreateStorage(address);
        state.Clear();
    }

    private sealed class DefaultableDictionary()
    {
        private bool _missingAreDefault;
        private readonly Dictionary<UInt256, StorageChangeTrace> _dictionary = new();
        public int EstimatedSize => _dictionary.Count + (_missingAreDefault ? 1 : 0);
        public bool HasClear => _missingAreDefault;
        public int Capacity => _dictionary.Capacity;

        public void Reset()
        {
            _missingAreDefault = false;
            _dictionary.Clear();
        }
        public void ClearAndSetMissingAsDefault()
        {
            _missingAreDefault = true;
            _dictionary.Clear();
        }

        public ref StorageChangeTrace GetValueRefOrAddDefault(UInt256 storageCellIndex, out bool exists)
        {
            ref StorageChangeTrace value = ref CollectionsMarshal.GetValueRefOrAddDefault(_dictionary, storageCellIndex, out exists);
            if (!exists && _missingAreDefault)
            {
                // Where we know the rest of the tree is empty
                // we can say the value was found but is default
                // rather than having to check the database
                value = StorageChangeTrace.ZeroBytes;
                exists = true;
            }
            return ref value;
        }

        public ref StorageChangeTrace GetValueRefOrNullRef(UInt256 storageCellIndex)
            => ref CollectionsMarshal.GetValueRefOrNullRef(_dictionary, storageCellIndex);

        public StorageChangeTrace this[UInt256 key]
        {
            set => _dictionary[key] = value;
        }

        public Dictionary<UInt256, StorageChangeTrace>.Enumerator GetEnumerator() => _dictionary.GetEnumerator();

        public void UnmarkClear()
        {
            _missingAreDefault = false;
        }
    }

    private sealed class PerContractState : IReturnable
    {
        private IWorldStateScopeProvider.IStorageTree? _backend;

        private readonly DefaultableDictionary BlockChange = new();
        private bool _wasWritten = false;
        private PersistentStorageProvider _provider;
        private Address _address;

        private PerContractState(Address address, PersistentStorageProvider provider) => Initialize(address, provider);

        private void Initialize(Address address, PersistentStorageProvider provider)
        {
            _address = address;
            _provider = provider;
        }

        public int EstimatedChanges => BlockChange.EstimatedSize;

        public Hash256 StorageRoot
        {
            get
            {
                EnsureStorageTree();
                return _backend.RootHash;
            }
        }

        public bool IsEmpty
        {
            get
            {
                // _backend.RootHash is not reflected until after commit, but this need to be reflected before commit
                // for SelfDestruct, since the deletion is not part of changelog, it need to be handled here.
                if (BlockChange.HasClear) return true;

                EnsureStorageTree();
                return _backend.RootHash == Keccak.EmptyTreeHash;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void EnsureStorageTree()
        {
            if (_backend is not null) return;
            CreateStorageTree();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateStorageTree()
        {
            _backend = _provider._currentScope.CreateStorageTree(_address);

            bool isEmpty = _backend.RootHash == Keccak.EmptyTreeHash;
            if (isEmpty && !_wasWritten)
            {
                // Slight optimization that skips the tree
                BlockChange.ClearAndSetMissingAsDefault();
            }
        }

        public void Clear()
        {
            EnsureStorageTree();
            BlockChange.ClearAndSetMissingAsDefault();
        }

        public void Return()
        {
            _address = null;
            _provider = null;
            _backend = null;
            _wasWritten = false;
            Pool.Return(this);
        }

        public void SaveChange(StorageCell storageCell, byte[] value)
        {
            _wasWritten = true;
            ref StorageChangeTrace valueChanges = ref BlockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
            if (!exists)
            {
                valueChanges = new StorageChangeTrace(value);
            }
            else
            {
                valueChanges = new StorageChangeTrace(valueChanges.Before, value);
            }
        }

        public byte[] LoadFromTree(in StorageCell storageCell)
        {
            ref StorageChangeTrace valueChange = ref BlockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
            if (!exists)
            {
                byte[] value = LoadFromTreeStorage(storageCell);

                valueChange = new(value, value);
            }
            else
            {
                Db.Metrics.IncrementStorageTreeCache();
            }

            return valueChange.After;
        }

        private byte[] LoadFromTreeStorage(StorageCell storageCell)
        {
            Db.Metrics.IncrementStorageTreeReads();

            EnsureStorageTree();
            return !storageCell.IsHash
                ? _backend.Get(storageCell.Index)
                : _backend.Get(storageCell.Hash);
        }

        public (int writes, int skipped) ProcessStorageChanges(IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch)
        {
            EnsureStorageTree();

            using IWorldStateScopeProvider.IStorageWriteBatch _ = storageWriteBatch;

            int writes = 0;
            int skipped = 0;

            if (BlockChange.HasClear)
            {
                storageWriteBatch.Clear();
                BlockChange.UnmarkClear(); // Note: Until the storage write batch is disposed, this BlockCache will pass read through the uncleared storage tree
            }

            foreach (var kvp in BlockChange)
            {
                byte[] after = kvp.Value.After;
                if (!Bytes.AreEqual(kvp.Value.Before, after) || kvp.Value.IsInitialValue)
                {
                    BlockChange[kvp.Key] = new(after, after);
                    storageWriteBatch.Set(kvp.Key, after);

                    writes++;
                }
                else
                {
                    skipped++;
                }
            }

            return (writes, skipped);
        }

        public void RemoveStorageTree()
        {
            _backend = null;
        }

        internal static PerContractState Rent(Address address, PersistentStorageProvider persistentStorageProvider)
            => Pool.Rent(address, persistentStorageProvider);

        private static class Pool
        {
            private static readonly ConcurrentQueue<PerContractState> _pool = [];
            private static int _poolCount;

            public static PerContractState Rent(Address address, PersistentStorageProvider provider)
            {
                if (Volatile.Read(ref _poolCount) > 0 && _pool.TryDequeue(out PerContractState item))
                {
                    Interlocked.Decrement(ref _poolCount);
                    item.Initialize(address, provider);
                    return item;
                }

                return new PerContractState(address, provider);
            }

            public static void Return(PerContractState item)
            {
                const int MaxItemSize = 512;
                const int MaxPooledCount = 2048;

                if (item.BlockChange.Capacity > MaxItemSize)
                    return;

                // shared pool fallback
                if (Interlocked.Increment(ref _poolCount) > MaxPooledCount)
                {
                    Interlocked.Decrement(ref _poolCount);
                    return;
                }

                item.BlockChange.Reset();
                _pool.Enqueue(item);
            }
        }
    }

    private readonly struct StorageChangeTrace
    {
        public static readonly StorageChangeTrace _zeroBytes = new(StorageTree.ZeroBytes, StorageTree.ZeroBytes);
        public static ref readonly StorageChangeTrace ZeroBytes => ref _zeroBytes;

        public StorageChangeTrace(byte[]? before, byte[]? after)
        {
            After = after ?? StorageTree.ZeroBytes;
            Before = before ?? StorageTree.ZeroBytes;
        }

        public StorageChangeTrace(byte[]? after)
        {
            After = after ?? StorageTree.ZeroBytes;
            Before = StorageTree.ZeroBytes;
            IsInitialValue = true;
        }

        public readonly byte[] Before;
        public readonly byte[] After;
        public readonly bool IsInitialValue;
    }
}
