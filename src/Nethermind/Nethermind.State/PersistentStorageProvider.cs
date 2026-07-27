// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State;

/// <summary>
/// Manages persistent storage allowing for snapshotting and restoring
/// Persists data to ITrieStore
/// </summary>
internal sealed partial class PersistentStorageProvider(StateProvider stateProvider, ILogManager logManager, LocalMetrics metrics)
    : PartialStorageProviderBase(logManager)
{
    private IWorldStateScopeProvider.IScope? _currentScope;
    private readonly StateProvider _stateProvider = stateProvider;
    private readonly LocalMetrics _metrics = metrics;
    private const int StoragesInitialCapacity = 4_096;

    private Dictionary<AddressAsKey, PerContractState> _storages = new(StoragesInitialCapacity);
    // Handed back by a detached write-back once it is done with the map it took.
    private Dictionary<AddressAsKey, PerContractState>? _spareStorages;
    private readonly Dictionary<AddressAsKey, bool> _toUpdateRoots = [];

    /// <summary>
    /// <see href="https://eips.ethereum.org/EIPS/eip-1283"/>
    /// </summary>
    private readonly Dictionary<StorageCell, byte[]> _originalValues = [];
    private readonly HashSet<AddressAsKey> _destroyedThisRound = [];
    private readonly HashSet<StorageCell> _committedThisRound = [];
    private readonly List<StorageClearChange> _storageClearJournal = [];

    // Zero means never captured, which is what a default BlockChange entry carries.
    private uint _originalsRound = 1;

    private void EndOriginalsRound()
    {
        _originalValues.ClearAndTrim();
        if (++_originalsRound == 0) _originalsRound = 1;
    }

    /// <summary>
    /// Reset the storage state
    /// </summary>
    public override void Reset(bool resetBlockChanges = true)
    {
        if (!resetBlockChanges)
        {
            for (int i = _storageClearJournal.Count - 1; i >= 0; i--)
            {
                RestoreStorageClear(i);
            }
        }

        _storageClearJournal.Clear();
        base.Reset();
        EndOriginalsRound();
        _committedThisRound.ClearAndTrim();
        _destroyedThisRound.ClearAndTrim();
        if (resetBlockChanges)
        {
            _storages.ResetAndClear();
            InvalidateStorageMemo();
            _toUpdateRoots.Clear();
        }
    }

    public void SetBackendScope(IWorldStateScopeProvider.IScope? scope) => _currentScope = scope;

    private IWorldStateScopeProvider.IScope CurrentScope =>
        _currentScope ?? throw new InvalidOperationException("Persistent storage can only be used within a world-state scope.");

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        IWorldStateScopeProvider.IScope currentScope = CurrentScope;
        _metrics.IncrementStorageWrites();
        // Pair with HasStorageToClear: cached writes can bypass LoadFromTree, so register before journaling.
        PerContractState state = GetOrCreateStorage(storageCell.Address);
        base.Set(in storageCell, newValue);
        // Populator executions never commit, so commit-time hints arrive too late.
        ValueAddress address = new(storageCell.Address.Bytes);
        currentScope.HintWarmSlot(in address, storageCell.Index);
        // Storage-root changes also rewrite the account leaf, including contracts only touched through storage.
        if (state.TakeAccountWarmHint()) currentScope.HintWarmAccount(in address);
    }

    /// <summary>
    /// Get the current value at the specified location
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <returns>Value at location</returns>
    protected override ReadOnlySpan<byte> GetCurrentValue(in StorageCell storageCell) =>
        TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes : LoadFromTree(storageCell);

    /// <summary>
    /// Return the original persistent storage value from the storage cell
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
    {
        if (!_originalValues.TryGetValue(storageCell, out byte[]? value))
        {
            throw new InvalidOperationException("Get original should only be called after get within the same caching round");
        }

        if (_intraBlockCache.TryGetValue(storageCell, out HeadChange head))
        {
            int currentSnapshot = _transactionChangesSnapshots.TryPeek(out int s) ? s : Resettable.EmptyPosition;
            if (head.CurrentIdx <= currentSnapshot)
            {
                // An untouched cell's current value is its transaction original.
                return head.Value;
            }

            // -1 denotes the block-level original; otherwise use the transaction-start value.
            return head.OriginalIdx != -1 ? _changes[head.OriginalIdx].Value : value;
        }

        return value;
    }

    public Hash256 GetStorageRoot(Address address) => GetOrCreateStorage(address).StorageRoot;

    private HashSet<AddressAsKey>? _tempToUpdateRoots;
    /// <summary>
    /// Called by Commit
    /// Used for persistent storage specific logic
    /// </summary>
    /// <param name="tracer">Storage tracer</param>
    protected override void CommitCore(IStorageTracer tracer)
    {
        if (_logger.IsTrace) _logger.Trace("Committing storage changes");

        int currentPosition = _changes.Count - 1;
        if (currentPosition < 0)
        {
            _destroyedThisRound.ClearAndTrim();
            return;
        }
        if (_changes[currentPosition].IsNull)
        {
            throw new InvalidOperationException($"Change at current position {currentPosition} was null when committing {nameof(PartialStorageProviderBase)}");
        }

        HashSet<AddressAsKey> toUpdateRoots = (_tempToUpdateRoots ??= []);

        ReadOnlySpan<Change> changes = CollectionsMarshal.AsSpan(_changes);
        Dictionary<StorageCell, StorageChangeTrace>? trace;
        if (tracer.IsTracingStorage)
        {
            trace = [];
            if (_destroyedThisRound.Count == 0)
            {
                CommitChanges<OnFlag, OffFlag>(changes, toUpdateRoots, trace);
            }
            else
            {
                CommitChanges<OnFlag, OnFlag>(changes, toUpdateRoots, trace);
            }
        }
        else
        {
            trace = null;
            if (_destroyedThisRound.Count == 0)
            {
                CommitChanges<OffFlag, OffFlag>(changes, toUpdateRoots, null);
            }
            else
            {
                CommitChanges<OffFlag, OnFlag>(changes, toUpdateRoots, null);
            }
        }

        foreach (AddressAsKey address in toUpdateRoots)
        {
            // EIP-158 can remove empty accounts.
            if (_stateProvider.AccountExists(address))
            {
                _toUpdateRoots[address] = true;
                // Create the tree before parallel access because _storages is not concurrent.
                GetOrCreateStorage(address).EnsureStorageTree();
            }
            else
            {
                _toUpdateRoots.Remove(address);
                if (_storages.TryGetValue(address, out PerContractState? storage))
                {
                    // Retain BlockChange so DefaultableDictionary preserves the self-destruct marker.
                    storage.RemoveStorageTree();
                }
            }
        }
        toUpdateRoots.Clear();

        if (trace is not null)
        {
            foreach ((StorageCell cell, byte[] originalValue) in _originalValues)
            {
                if (trace.TryGetValue(cell, out StorageChangeTrace changeTrace))
                {
                    trace[cell] = new StorageChangeTrace(originalValue, changeTrace.After);
                }
                else
                {
                    tracer.ReportStorageRead(cell);
                }
            }
        }

        base.CommitCore(tracer);
        EndOriginalsRound();
        _committedThisRound.ClearAndTrim();
        _destroyedThisRound.ClearAndTrim();
        _storageClearJournal.Clear();

        if (trace is not null)
        {
            ReportChanges(tracer, trace);
        }
    }

    private void CommitChanges<TStorageTracing, HasDestroyedAccounts>(
        ReadOnlySpan<Change> changes,
        HashSet<AddressAsKey> toUpdateRoots,
        Dictionary<StorageCell, StorageChangeTrace>? trace)
        where TStorageTracing : struct, IFlag
        where HasDestroyedAccounts : struct, IFlag
    {
        Debug.Assert(TStorageTracing.IsActive == (trace is not null));
        Debug.Assert(HasDestroyedAccounts.IsActive == (_destroyedThisRound.Count != 0));

        for (int i = changes.Length - 1; i >= 0; i--)
        {
            ref readonly Change change = ref changes[i];
            if (change.ChangeType == StorageChangeType.StorageClear)
            {
                continue;
            }

            if (!_committedThisRound.Add(change.StorageCell))
            {
                continue;
            }

            // Debug-only: A broken index surfaces anyway as a storage-root mismatch on the block.
            Debug.Assert(_intraBlockCache[change.StorageCell].CurrentIdx == i,
                $"Expected the cached index to equal {i}");

            if (change.ChangeType == StorageChangeType.Update)
            {
                // A SaveChange would resurrect the dead value over the Clear() marker;
                // tracers still see the cell zeroed, as the journaled path reported it.
                if (HasDestroyedAccounts.IsActive && _destroyedThisRound.Contains(change.StorageCell.Address))
                {
                    if (TStorageTracing.IsActive)
                    {
                        RequireTrace(trace)[change.StorageCell] = new StorageChangeTrace(StorageTree.ZeroBytes);
                    }

                    continue;
                }

                if (_logger.IsTrace)
                {
                    TraceUpdate(change);
                }

                if (_originalValues.TryGetValue(change.StorageCell, out byte[]? initialValue) &&
                    initialValue.AsSpan().SequenceEqual(change.Value))
                {
                    // no need to update the tree if the value is the same
                }
                else
                {
                    toUpdateRoots.Add(change.StorageCell.Address);

                    GetOrCreateStorage(change.StorageCell.Address)
                        .SaveChange(change.StorageCell, change.Value);
                }

                if (TStorageTracing.IsActive)
                {
                    RequireTrace(trace)[change.StorageCell] = new StorageChangeTrace(change.Value);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceUpdate(in Change change)
        => _logger.Trace($"  Update {change.StorageCell.Address}_{change.StorageCell.Index} V = {change.Value.ToHexString(true)}");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Dictionary<StorageCell, StorageChangeTrace> RequireTrace(Dictionary<StorageCell, StorageChangeTrace>? trace)
        => trace ?? throw new InvalidOperationException("Storage tracing is active without a trace accumulator.");

    internal void FlushToTree(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        if (_toUpdateRoots.Count == 0)
            return;

        UpdateRootHashes(writeBatch);

        _toUpdateRoots.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private partial void UpdateRootHashes(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch);

    private void UpdateRootHashesSingleThread(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        foreach (KeyValuePair<AddressAsKey, bool> kvp in _toUpdateRoots)
        {
            if (!kvp.Value) continue;

            if (!_storages.TryGetValue(kvp.Key, out PerContractState? contractState))
            {
                Debug.Fail($"Storage root marked changed for {kvp.Key} but no contract state is present");
                continue;
            }

            (int writes, int skipped) = contractState.ProcessStorageChanges(
                writeBatch.CreateStorageWriteBatch(kvp.Key, contractState.EstimatedChanges));

            ReportMetrics(writes, skipped);
        }
    }

    // Worker finalizers call this, so it must not use non-atomic per-scope metrics.
    private static void ReportMetrics(int writes, int skipped)
    {
        if (skipped > 0)
            Db.Metrics.IncrementStorageSkippedWrites(skipped);

        if (writes > 0)
            Db.Metrics.IncrementStorageTreeWrites(writes);
    }

    /// <summary>Drops the block's storage changes, returning each contract's state to the pool.</summary>
    /// <remarks>
    /// Only a block that took no snapshot has states to return here, and it pays for them on its own thread. One that
    /// detached its changes left an empty map behind, and its states belong to the snapshot, which returns them once
    /// written; that is also what keeps a state from being returned twice.
    /// </remarks>
    public void ClearStorageMap()
    {
        _storages.ResetAndClear();
        InvalidateStorageMemo();
    }

    /// <summary>
    /// Hands over the block's storage changes, leaving an empty map in their place, with each contract's block-end
    /// fate already resolved so the snapshot needs nothing from the state provider afterwards.
    /// </summary>
    /// <remarks>
    /// A handover rather than a copy: <see cref="ClearStorageMap"/> drops the map at the end of every commit anyway.
    /// The two references that outlive the map are dealt with: the last-contract memo is invalidated here, and the
    /// storage clear journal is emptied by the commit before this runs.
    /// </remarks>
    /// <returns>The changes; the caller owns the snapshot and must dispose it.</returns>
    internal IWorldStateScopeProvider.IBlockChangeSnapshot DetachBlockChanges()
    {
        foreach (KeyValuePair<AddressAsKey, PerContractState> storage in _storages)
        {
            storage.Value.BlockEndFate = FateOf(storage);
        }

        Dictionary<AddressAsKey, PerContractState> storages = _storages;
        _storages = Interlocked.Exchange(ref _spareStorages, null) ?? new Dictionary<AddressAsKey, PerContractState>(StoragesInitialCapacity);
        InvalidateStorageMemo();
        return new StorageChangeSnapshot(this, storages, _stateProvider.DetachRemovedAccountsWithStorage());
    }

    /// <summary>
    /// The storage half of a committed block's changes, detached from the provider that produced it.
    /// </summary>
    /// <remarks>
    /// Owns the contract states until disposed, when it returns them to their pool. The provider gives them up once
    /// the block is committed, so nothing else reads or mutates them and the snapshot may be written on another
    /// thread, after the scope that produced it has been disposed. Nothing it touches may reach that scope.
    /// </remarks>
    private sealed class StorageChangeSnapshot(
        PersistentStorageProvider provider,
        Dictionary<AddressAsKey, PerContractState> storages,
        List<AddressAsKey> removedWithStorage) : IWorldStateScopeProvider.IBlockChangeSnapshot
    {
        /// <summary>
        /// Writes the block's final value of every slot touched into <paramref name="writeBatch"/>, after a clear for
        /// every contract whose pre-block storage the block wiped or whose account it removed.
        /// </summary>
        /// <remarks>
        /// Clears come first, so none may follow a slot write of the same write-back. An account removed with storage
        /// the block never touched has no contract state, so the removals the state provider recorded are checked as
        /// well. A batch that drops what it held on a clear ends the write there: it has nothing left to complete, and
        /// the block's own slots are all that would go back in.
        /// </remarks>
        public void WriteTo(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
        {
            if (!writeBatch.AcceptsStorageWrites) return;

            foreach (AddressAsKey removed in removedWithStorage)
            {
                // Only a block that saw the storage empty before touching it can prove the cache holds none of its slots.
                if (!storages.TryGetValue(removed, out PerContractState? state) || state.HadStorageBeforeBlock != false)
                {
                    ClearCachedStorage(writeBatch, removed.Value);
                    if (!writeBatch.AcceptsStorageWrites) return;
                }
            }

            foreach (KeyValuePair<AddressAsKey, PerContractState> storage in storages)
            {
                PerContractState state = storage.Value;
                if (state.BlockEndFate == AccountFate.Unknown || state.ClearsPreBlockStorage(state.BlockEndFate == AccountFate.Present))
                {
                    ClearCachedStorage(writeBatch, storage.Key.Value);
                    if (!writeBatch.AcceptsStorageWrites) return;
                }
            }

            if (!writeBatch.AcceptsStorageWrites) return;

            foreach (KeyValuePair<AddressAsKey, PerContractState> storage in storages)
            {
                if (storage.Value.BlockEndFate == AccountFate.Present) storage.Value.WriteSlots(writeBatch);
            }
        }

        public void Dispose()
        {
            // The write-back thread, so recycling costs the block nothing on the path that gets here; the blocks
            // that take no snapshot pay for it in ClearStorageMap instead. Each state was rented by the block that
            // filled it and is released once, which is what keeps the pool sound.
            storages.ResetAndClear();
            Volatile.Write(ref provider._spareStorages, storages);
            provider._stateProvider.ReturnRemovedAccounts(removedWithStorage);
        }
    }

    private enum AccountFate { Present, Removed, Unknown }

    private static void ClearCachedStorage(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, Address address)
    {
        using IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch = writeBatch.CreateStorageWriteBatch(address, 0);
        storageWriteBatch.Clear();
    }

    // An address with storage touched but no account record was read directly, so it exists unchanged, unless it was
    // also written: execution always loads an account before writing its storage, so such a contract has no state the
    // caches can vouch for, and its cached pre-block slots must go rather than be trusted.
    private AccountFate FateOf(KeyValuePair<AddressAsKey, PerContractState> storage) =>
        _stateProvider.HasAccountAtBlockEnd(storage.Key.Value) switch
        {
            true => AccountFate.Present,
            false => AccountFate.Removed,
            null => storage.Value.WasWritten ? AccountFate.Unknown : AccountFate.Present,
        };

    private Address? _lastStorageAddress;
    private PerContractState? _lastStorage;

    private void InvalidateStorageMemo()
    {
        _lastStorageAddress = null;
        _lastStorage = null;
    }

    private PerContractState GetOrCreateStorage(Address address)
    {
        if (_lastStorageAddress == address && _lastStorage is not null)
        {
            return _lastStorage;
        }

        ref PerContractState? value = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (!exists) value = PerContractState.Rent(address, this);
        PerContractState storage = value ?? ThrowNoStorageState(address);
        _lastStorageAddress = address;
        _lastStorage = storage;
        return storage;
    }

    [DoesNotReturn, StackTraceHidden]
    private static PerContractState ThrowNoStorageState(Address address) =>
        throw new InvalidOperationException($"No storage state is available for {address}.");

    public void WarmUp(in StorageCell storageCell, bool isEmpty)
    {
        if (!isEmpty)
        {
            LoadFromTree(in storageCell);
        }
    }

    private ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell) =>
        GetOrCreateStorage(storageCell.Address).LoadFromTree(storageCell);

    /// <summary>
    /// Captures the first read value for <see cref="GetOriginal"/> and commit-time
    /// <see cref="IStorageTracer.ReportStorageRead"/>; reads are not journaled.
    /// </summary>
    private void CaptureOriginalValue(in StorageCell cell, byte[] value)
    {
        ref byte[]? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_originalValues, cell, out bool exists);
        if (!exists)
        {
            slot = value;
        }
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
    /// Reports reads and clears original values when a read-only round has no journaled changes.
    /// </summary>
    public override void Commit(IStorageTracer tracer)
    {
        if (_changes.Count == 0)
        {
            if (_originalValues.Count != 0)
            {
                if (tracer.IsTracingStorage)
                {
                    foreach (StorageCell cell in _originalValues.Keys)
                    {
                        tracer.ReportStorageRead(cell);
                    }
                }

                EndOriginalsRound();
            }

            _destroyedThisRound.ClearAndTrim();
            return;
        }

        base.Commit(tracer);
    }

    public void MarkStorageDestroyed(Address address)
    {
        _destroyedThisRound.Add(address);
        ResetContractState(address);
    }

    private void ResetContractState(Address address)
    {
        IWorldStateScopeProvider.IScope currentScope = CurrentScope;
        _toUpdateRoots.TryAdd(address, true);
        PerContractState state = GetOrCreateStorage(address);
        state.Clear();
        if (state.TakeAccountWarmHint()) currentScope.HintWarmAccount(new ValueAddress(address.Bytes));
    }

    public override void ClearStorage(Address address)
    {
        IWorldStateScopeProvider.IScope currentScope = CurrentScope;
        if (!HasStorageToClear(address))
        {
            return;
        }

        List<KeyValuePair<StorageCell, byte[]>>? originalValues = null;
        foreach (KeyValuePair<StorageCell, byte[]> readCell in _originalValues)
        {
            if (readCell.Key.Address == address)
            {
                (originalValues ??= []).Add(readCell);
            }
        }

        base.ClearStorage(address);

        if (originalValues is not null)
        {
            foreach (KeyValuePair<StorageCell, byte[]> readCell in originalValues)
            {
                if (!_intraBlockCache.ContainsKey(readCell.Key))
                {
                    Set(readCell.Key, StorageTree.ZeroBytes);
                }
            }
        }

        bool? rootUpdate = _toUpdateRoots.TryGetValue(address, out bool currentRootUpdate) ? currentRootUpdate : null;
        PerContractState contractState = GetOrCreateStorage(address);
        DefaultableDictionary.ClearSnapshot blockChange = contractState.ClearRevertibly();
        _toUpdateRoots[address] = true;
        if (contractState.TakeAccountWarmHint()) currentScope.HintWarmAccount(new ValueAddress(address.Bytes));
        int journalIndex = _storageClearJournal.Count;
        _storageClearJournal.Add(new StorageClearChange(address, blockChange, originalValues, rootUpdate));
        PushStorageClear(journalIndex);
    }

    /// <summary>
    /// Determines whether <paramref name="address"/> has readable storage that must be cleared.
    /// </summary>
    /// <remarks>
    /// Reads and writes register the address in <see cref="_storages"/>; <see cref="Set"/> does so
    /// explicitly because a cached write can bypass the loading path. When the pending account has
    /// no storage root, reads can still resolve through the scope's pre-block account until account
    /// changes are flushed, so that backend account must also be checked.
    /// </remarks>
    private bool HasStorageToClear(Address address)
    {
        if (_storages.ContainsKey(address))
        {
            return true;
        }

        Account? account = _stateProvider.GetThroughCache(address);
        if (account?.HasStorage == true)
        {
            return true;
        }

        return CurrentScope.Get(address)?.HasStorage == true;
    }

    protected override void RestoreStorageClear(int journalIndex)
    {
        int lastIndex = _storageClearJournal.Count - 1;
        if ((uint)journalIndex >= (uint)_storageClearJournal.Count || journalIndex != lastIndex)
        {
            throw new InvalidOperationException($"Expected storage clear journal entry {lastIndex}, got {journalIndex}");
        }

        StorageClearChange change = _storageClearJournal[journalIndex];
        _storageClearJournal.RemoveAt(journalIndex);
        GetOrCreateStorage(change.Address).RestoreClear(change.BlockChange);

        foreach (StorageCell cell in _originalValues.Keys)
        {
            if (cell.Address == change.Address)
            {
                _originalValues.Remove(cell);
            }
        }

        if (change.OriginalValues is not null)
        {
            _originalValues.AddOrUpdateRange(change.OriginalValues);
        }

        if (change.RootUpdate is { } rootUpdate)
        {
            _toUpdateRoots[change.Address] = rootUpdate;
        }
        else
        {
            _toUpdateRoots.Remove(change.Address);
        }
    }

    private readonly record struct StorageClearChange(
        Address Address,
        DefaultableDictionary.ClearSnapshot BlockChange,
        List<KeyValuePair<StorageCell, byte[]>>? OriginalValues,
        bool? RootUpdate);

    private sealed class DefaultableDictionary()
    {
        private bool _missingAreDefault;
        private Dictionary<UInt256, StorageChangeTrace> _dictionary = new(Comparer.Instance);
        private Dictionary<UInt256, StorageChangeTrace>? _spare;
        public int EstimatedSize => _dictionary.Count + (_missingAreDefault ? 1 : 0);
        public int Count => _dictionary.Count;
        public bool HasClear => _missingAreDefault;

        public void Reset(int capacity)
        {
            _missingAreDefault = false;
            if (_spare is not null && _spare.Capacity > _dictionary.Capacity)
            {
                _dictionary = _spare;
            }

            _spare = null;
            _dictionary.ClearAndTrim(capacity, capacity);
        }
        public void ClearAndSetMissingAsDefault()
        {
            _missingAreDefault = true;
            _dictionary.Clear();
        }

        public ClearSnapshot ClearRevertibly()
        {
            Dictionary<UInt256, StorageChangeTrace>? previousEntries = null;
            if (_dictionary.Count != 0)
            {
                previousEntries = _dictionary;
                _dictionary = _spare ?? new Dictionary<UInt256, StorageChangeTrace>(Comparer.Instance);
                _spare = null;
            }

            ClearSnapshot snapshot = new(previousEntries, _missingAreDefault);
            _missingAreDefault = true;
            return snapshot;
        }

        public void Restore(ClearSnapshot snapshot)
        {
            if (snapshot.PreviousEntries is not null)
            {
                _dictionary.Clear();
                if (_spare is null || _dictionary.Capacity > _spare.Capacity)
                {
                    _spare = _dictionary;
                }

                _dictionary = snapshot.PreviousEntries;
            }
            else
            {
                _dictionary.Clear();
            }

            _missingAreDefault = snapshot.MissingAreDefault;
        }

        public ref StorageChangeTrace GetValueRefOrAddDefault(UInt256 storageCellIndex, out bool exists)
        {
            ref StorageChangeTrace value = ref CollectionsMarshal.GetValueRefOrAddDefault(_dictionary, storageCellIndex, out exists);
            if (!exists && _missingAreDefault)
            {
                // A known-empty tree needs no database lookup for a missing value.
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

        private sealed class Comparer : IEqualityComparer<UInt256>
        {
            public static Comparer Instance { get; } = new();

            private Comparer() { }

            public bool Equals(UInt256 x, UInt256 y) => x.Equals(in y);

            public int GetHashCode([DisallowNull] UInt256 obj)
                => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in obj, 1)).FastHash();
        }

        public void UnmarkClear() => _missingAreDefault = false;

        public readonly record struct ClearSnapshot(
            Dictionary<UInt256, StorageChangeTrace>? PreviousEntries,
            bool MissingAreDefault);
    }

    private sealed class PerContractState : IReturnable
    {
        private IWorldStateScopeProvider.IStorageTree? _backend;

        private readonly DefaultableDictionary BlockChange = new();
        private bool _wasWritten = false;
        // Whether the contract held storage before the block and whether the block cleared it: together they say if a
        // cache of pre-block slots must drop them. Captured at the first tree creation, before any flush moves the root.
        private bool _hadStorageBeforeBlock;
        private bool _storageRootSeen;
        private bool _wasCleared;
        private bool _accountHinted;
        private PersistentStorageProvider? _provider;
        private Address? _address;

        private PerContractState(Address address, PersistentStorageProvider provider) => Initialize(address, provider);

        private void Initialize(Address address, PersistentStorageProvider provider)
        {
            _address = address;
            _provider = provider;
        }

        public int EstimatedChanges => BlockChange.EstimatedSize;

        private PersistentStorageProvider Provider =>
            _provider ?? throw new InvalidOperationException("A returned storage state cannot be used.");

        private Address Address =>
            _address ?? throw new InvalidOperationException("A returned storage state cannot be used.");

        public bool WasWritten => _wasWritten;

        /// <summary>
        /// Claims the one account trie warm hint this contract needs for the block.
        /// </summary>
        /// <remarks>
        /// Spent whether or not the scope keeps the hint. A scope that drops this one drops the block's slot hints as
        /// well, because both are gated on the same disposed and paused flags, so the contract is never left with its
        /// slots warmed and its account path cold.
        /// </remarks>
        /// <returns><see langword="true"/> for the first caller, <see langword="false"/> for every later one.</returns>
        public bool TakeAccountWarmHint()
        {
            if (_accountHinted) return false;
            _accountHinted = true;
            return true;
        }

        /// <summary>The account's fate at block end.</summary>
        /// <remarks>
        /// Resolved by <see cref="DetachBlockChanges"/> on the block thread, while the state provider still holds the
        /// block's account record. The detached snapshot is written after the scope is gone, so it must not reach back
        /// into the provider to work this out for itself.
        /// </remarks>
        public AccountFate BlockEndFate { get; set; }

        public Hash256 StorageRoot
        {
            get
            {
                EnsureStorageTree();
                return _backend.RootHash;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [MemberNotNull(nameof(_backend))]
        internal void EnsureStorageTree()
        {
            if (_backend is not null) return;
            CreateStorageTree();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [MemberNotNull(nameof(_backend))]
        private void CreateStorageTree()
        {
            _backend = Provider.CurrentScope.CreateStorageTree(Address);

            bool isEmpty = _backend.IsKnownEmpty;
            if (!_storageRootSeen)
            {
                _storageRootSeen = true;
                _hadStorageBeforeBlock = !isEmpty;
            }

            if (isEmpty && !_wasWritten)
            {
                // Mark all missing cells as zero to avoid tree lookups.
                BlockChange.ClearAndSetMissingAsDefault();
            }
        }

        public void Clear()
        {
            EnsureStorageTree();
            _wasCleared = true;
            BlockChange.ClearAndSetMissingAsDefault();
        }

        public DefaultableDictionary.ClearSnapshot ClearRevertibly()
        {
            EnsureStorageTree();
            // Stays set if the clear is reverted: a cache then drops slots it could have kept, never keeps stale ones.
            _wasCleared = true;
            return BlockChange.ClearRevertibly();
        }

        public void RestoreClear(DefaultableDictionary.ClearSnapshot snapshot) => BlockChange.Restore(snapshot);

        public void Return()
        {
            _address = null;
            _provider = null;
            _backend = null;
            _wasWritten = false;
            _hadStorageBeforeBlock = false;
            _storageRootSeen = false;
            _wasCleared = false;
            _accountHinted = false;
            // A later block may never detach its changes, and would then read whatever this one left behind.
            BlockEndFate = AccountFate.Present;
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

            EnsureStorageTree();
            _backend.HintSet(storageCell.Index, value);
        }

        public ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell)
        {
            ref StorageChangeTrace valueChange = ref BlockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
            if (!exists)
            {
                byte[] value = LoadFromTreeStorage(storageCell);

                valueChange = new(value, value);
            }
            else
            {
                Provider._metrics.IncrementStorageTreeCache();
            }

            PersistentStorageProvider provider = Provider;
            uint round = provider._originalsRound;
            if (valueChange.CapturedRound != round)
            {
                provider.CaptureOriginalValue(storageCell, valueChange.After);
                valueChange = valueChange.WithCapturedRound(round);
            }

            return valueChange.After;
        }

        private byte[] LoadFromTreeStorage(StorageCell storageCell)
        {
            Provider._metrics.IncrementStorageTreeReads();

            EnsureStorageTree();
            return _backend.Get(storageCell.Index);
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
                // Reads must continue through the uncleared tree until the write batch is disposed.
                BlockChange.UnmarkClear();
            }

            // Delete last to match stateless verifiers and avoid resolving siblings after branch compression.
            // Deletes are rare, so rent the pooled array only when the first one is added.

            using ArrayPoolListRef<KeyValuePair<UInt256, StorageChangeTrace>> deferredDeletes = new(0);

            foreach (KeyValuePair<UInt256, StorageChangeTrace> kvp in BlockChange)
            {
                byte[] after = kvp.Value.After;
                if (!Bytes.AreEqual(kvp.Value.Before, after) || kvp.Value.IsInitialValue)
                {
                    if (after.IsZero())
                    {
                        deferredDeletes.Add(kvp);
                    }
                    else
                    {
                        // Safe during enumeration: this overwrites an existing key only.
                        BlockChange[kvp.Key] = new(after, after);
                        storageWriteBatch.Set(kvp.Key, after);

                        writes++;
                    }
                }
                else
                {
                    skipped++;
                }
            }

            foreach (KeyValuePair<UInt256, StorageChangeTrace> kvp in deferredDeletes.AsSpan())
            {
                byte[] after = kvp.Value.After;
                BlockChange[kvp.Key] = new(after, after);
                storageWriteBatch.Set(kvp.Key, after);

                writes++;
            }

            return (writes, skipped);
        }

        /// <summary>Whether the contract held storage before the block, or <see langword="null"/> when the block never resolved its tree.</summary>
        public bool? HadStorageBeforeBlock => _storageRootSeen ? _hadStorageBeforeBlock : null;

        /// <summary>
        /// Whether a cache of the contract's pre-block slots must drop them: the block cleared storage the contract held
        /// before it, or removed the account. A contract without storage had no slots to cache.
        /// </summary>
        public bool ClearsPreBlockStorage(bool accountExists) => _hadStorageBeforeBlock && (_wasCleared || !accountExists);

        /// <summary>Writes the block's final value of every slot touched, reads included, into <paramref name="writeBatch"/>.</summary>
        /// <remarks>
        /// Reads only this contract's address and recorded changes. It must never resolve the storage tree, as
        /// <see cref="ProcessStorageChanges"/> does: the scope that owns the tree may already have been disposed by
        /// the time a detached snapshot is written.
        /// </remarks>
        public void WriteSlots(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
        {
            if (BlockChange.Count == 0) return;

            using IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch = writeBatch.CreateStorageWriteBatch(Address, BlockChange.Count);
            foreach (KeyValuePair<UInt256, StorageChangeTrace> kvp in BlockChange)
            {
                storageWriteBatch.Set(kvp.Key, kvp.Value.After);
            }
        }

        public void RemoveStorageTree() => _backend = null;

        internal static PerContractState Rent(Address address, PersistentStorageProvider persistentStorageProvider)
            => Pool.Rent(address, persistentStorageProvider);

        private static class Pool
        {
            private static readonly ConcurrentQueue<PerContractState> _pool = [];
            private static int _poolCount;

            public static PerContractState Rent(Address address, PersistentStorageProvider provider)
            {
                if (Volatile.Read(ref _poolCount) > 0 && _pool.TryDequeue(out PerContractState? item))
                {
                    Interlocked.Decrement(ref _poolCount);
                    item.Initialize(address, provider);
                    return item;
                }

                return new PerContractState(address, provider);
            }

            public static void Return(PerContractState item)
            {
                const int PooledDictionaryCapacity = 512;
                const int MaxPooledCount = 2048;

                if (Interlocked.Increment(ref _poolCount) > MaxPooledCount)
                {
                    Interlocked.Decrement(ref _poolCount);
                    return;
                }

                item.BlockChange.Reset(PooledDictionaryCapacity);
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

        private StorageChangeTrace(byte[] before, byte[] after, bool isInitialValue, uint capturedRound)
        {
            Before = before;
            After = after;
            IsInitialValue = isInitialValue;
            CapturedRound = capturedRound;
        }

        public StorageChangeTrace WithCapturedRound(uint round) => new(Before, After, IsInitialValue, round);

        public readonly byte[] Before;
        public readonly byte[] After;
        public readonly bool IsInitialValue;
        public readonly uint CapturedRound;
    }
}
