// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Logging;
using Nethermind.Int256;

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
    /// EIP-1283: tracks original value per storage cell for gas calculations.
    /// Entries are version-stamped with <see cref="_commitRound"/> so we skip per-tx Clear().
    /// </summary>
    private readonly Dictionary<InternalStorageKey, OriginalValue> _originalValues = new(1_024);

    private int _commitRound;

    private readonly HashSet<InternalStorageKey> _committedThisRound = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct OriginalValue
    {
        public StorageValue Value;
        public int Round;
    }

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
        _commitRound = 0;
        _committedThisRound.Clear();
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
    [SkipLocalsInit]
    protected override StorageValue GetCurrentValue(in StorageCell storageCell) =>
        TryGetCachedValue(storageCell, out StorageValue cached) ? cached : LoadFromTree(storageCell);

    /// <summary>
    /// Return the original persistent storage value from the storage cell
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    [SkipLocalsInit]
    public StorageValue GetOriginal(in StorageCell storageCell)
    {
        InternalStorageKey ikey = new(in storageCell);
        ref OriginalValue entry = ref CollectionsMarshal.GetValueRefOrNullRef(_originalValues, ikey);
        if (Unsafe.IsNullRef(ref entry) || entry.Round != _commitRound)
        {
            throw new InvalidOperationException("Get original should only be called after get within the same caching round");
        }

        if (_transactionChangesSnapshots.TryPeek(out int snapshot))
        {
            if (TryGetStack(storageCell, out StackList<int> stack))
            {
                if (stack.TryGetSearchedItem(snapshot, out int lastChangeIndexBeforeOriginalSnapshot))
                {
                    return _changeValues[lastChangeIndexBeforeOriginalSnapshot];
                }
            }
        }

        return entry.Value;
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
    [SkipLocalsInit]
    protected override void CommitCore(IStorageTracer tracer)
    {
        if (_logger.IsTrace) _logger.Trace("Committing storage changes");

        int currentPosition = _changeCount - 1;
        if (currentPosition < 0)
        {
            return;
        }
        ReadOnlySpan<ChangeKey> keys = _changeKeys.AsSpan(0, _changeCount);
        ReadOnlySpan<StorageValue> values = _changeValues.AsSpan(0, _changeCount);
        if (keys[currentPosition].IsNull)
        {
            throw new InvalidOperationException($"Change at current position {currentPosition} was null when committing {nameof(PartialStorageProviderBase)}");
        }

        HashSet<AddressAsKey> toUpdateRoots = (_tempToUpdateRoots ??= new());

        bool isTracing = tracer.IsTracingStorage;
        Dictionary<StorageCell, TracingStorageChange>? trace = null;
        if (isTracing)
        {
            trace = [];
        }
        for (int i = 0; i <= currentPosition; i++)
        {
            int pos = currentPosition - i;
            ref readonly ChangeKey key = ref keys[pos];
            ref readonly StorageValue value = ref values[pos];
            if (!isTracing && key.ChangeType == ChangeType.JustCache)
            {
                continue;
            }

            InternalStorageKey ikey = new(in key.StorageCell);
            if (_committedThisRound.Contains(ikey))
            {
                if (isTracing && key.ChangeType == ChangeType.JustCache)
                {
                    trace![key.StorageCell] = new TracingStorageChange(value, trace[key.StorageCell].After);
                }

                continue;
            }

            if (isTracing && key.ChangeType == ChangeType.JustCache)
            {
                tracer!.ReportStorageRead(key.StorageCell);
            }

            _committedThisRound.Add(ikey);
            int forAssertion = GetStack(in ikey).Pop();
            if (forAssertion != pos)
            {
                throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {currentPosition} - {i}");
            }

            if (key.ChangeType == ChangeType.Update)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"  Update {key.StorageCell.Address}_{key.StorageCell.Index} V = {value.ToEvmBytes().ToHexString(true)}");
                }

                ref OriginalValue originalEntry = ref CollectionsMarshal.GetValueRefOrNullRef(_originalValues, ikey);
                if (!Unsafe.IsNullRef(ref originalEntry) && originalEntry.Round == _commitRound && originalEntry.Value == value)
                {
                    // no need to update the tree if the value is the same
                }
                else
                {
                    toUpdateRoots.Add(key.StorageCell.Address);

                    GetOrCreateStorage(key.StorageCell.Address)
                        .SaveChange(key.StorageCell, value);
                }

                if (isTracing)
                {
                    trace![key.StorageCell] = new TracingStorageChange(StorageValue.Zero, value);
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
        _commitRound++;
        _committedThisRound.Clear();

        if (isTracing)
        {
            ReportChanges(tracer!, trace!);
        }
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
            LoadFromTree(in storageCell);
        }
    }

    [SkipLocalsInit]
    private StorageValue LoadFromTree(in StorageCell storageCell)
    {
        return GetOrCreateStorage(storageCell.Address).LoadFromTree(storageCell);
    }

    [SkipLocalsInit]
    private void PushToRegistryOnly(in StorageCell cell, StorageValue value)
    {
        StackList<int> stack = SetupRegistry(cell);
        InternalStorageKey ikey = new(in cell);
        ref OriginalValue entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_originalValues, ikey, out _);
        entry.Value = value;
        entry.Round = _commitRound;
        stack.Push(_changeCount);
        EnsureChangeCapacity();
        _changeKeys[_changeCount] = new ChangeKey(in cell, ChangeType.JustCache);
        _changeValues[_changeCount] = value;
        _changeCount++;
    }

    private static void ReportChanges(IStorageTracer tracer, Dictionary<StorageCell, TracingStorageChange> trace)
    {
        foreach ((StorageCell address, TracingStorageChange change) in trace)
        {
            if (change.Before != change.After)
            {
                tracer.ReportStorageChange(address, change.Before.ToEvmBytes(), change.After.ToEvmBytes());
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
        private readonly Dictionary<UInt256, StorageChangeTrace> _dictionary = new(Comparer.Instance);
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
                // value is already zeroed by GetValueRefOrAddDefault, which matches StorageValue.Zero
                exists = true;
            }
            return ref value;
        }

        public ref StorageChangeTrace GetValueRefOrNullRef(UInt256 storageCellIndex)
            => ref CollectionsMarshal.GetValueRefOrNullRef(_dictionary, storageCellIndex);

        public Dictionary<UInt256, StorageChangeTrace>.KeyCollection Keys => _dictionary.Keys;

        public Dictionary<UInt256, StorageChangeTrace>.Enumerator GetEnumerator() => _dictionary.GetEnumerator();

        private sealed class Comparer : IEqualityComparer<UInt256>
        {
            public static Comparer Instance { get; } = new();

            private Comparer() { }

            public bool Equals(UInt256 x, UInt256 y)
                => Unsafe.As<UInt256, Vector256<byte>>(ref x) == Unsafe.As<UInt256, Vector256<byte>>(ref y);

            public int GetHashCode([DisallowNull] UInt256 obj)
                => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in obj, 1)).FastHash();
        }

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

        [SkipLocalsInit]
        public void SaveChange(StorageCell storageCell, StorageValue value)
        {
            _wasWritten = true;
            ref StorageChangeTrace valueChanges = ref BlockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
            if (!exists)
            {
                valueChanges = new StorageChangeTrace { After = value, IsDirty = true, IsInitialValue = true };
            }
            else
            {
                valueChanges.After = value;
                valueChanges.IsDirty = true;
            }
        }

        public StorageValue LoadFromTree(in StorageCell storageCell)
        {
            ref StorageChangeTrace valueChange = ref BlockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
            if (!exists)
            {
                StorageValue value = LoadFromTreeStorage(storageCell);

                valueChange = new StorageChangeTrace { After = value, IsDirty = false, IsInitialValue = false };
            }
            else
            {
                Db.Metrics.IncrementStorageTreeCache();
            }

            if (!storageCell.IsHash) _provider.PushToRegistryOnly(storageCell, valueChange.After);
            return valueChange.After;
        }

        private StorageValue LoadFromTreeStorage(StorageCell storageCell)
        {
            Db.Metrics.IncrementStorageTreeReads();

            EnsureStorageTree();
            return !storageCell.IsHash
                ? _backend.Get(storageCell.Index)
                : _backend.Get(storageCell.Hash);
        }

        [SkipLocalsInit]
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

            foreach (UInt256 key in BlockChange.Keys)
            {
                ref StorageChangeTrace entry = ref BlockChange.GetValueRefOrNullRef(key);
                if (entry.IsDirty || entry.IsInitialValue)
                {
                    storageWriteBatch.Set(key, in entry.After);
                    entry.IsDirty = false;
                    entry.IsInitialValue = false;

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
                const int MaxItemSize = 16_384;
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

    private struct StorageChangeTrace
    {
        public StorageValue After;
        public bool IsDirty;
        public bool IsInitialValue;
    }

    private readonly struct TracingStorageChange(StorageValue before, StorageValue after)
    {
        public readonly StorageValue Before = before;
        public readonly StorageValue After = after;
    }
}
