// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
    private IWorldStateScopeProvider.IScope _currentScope;
    private readonly StateProvider _stateProvider = stateProvider;
    private readonly LocalMetrics _metrics = metrics;
    private readonly Dictionary<AddressAsKey, PerContractState> _storages = new(4_096);
    private readonly Dictionary<AddressAsKey, bool> _toUpdateRoots = [];

    /// <summary>
    /// <see href="https://eips.ethereum.org/EIPS/eip-1283"/>
    /// </summary>
    private readonly Dictionary<StorageCell, byte[]> _originalValues = [];
    private readonly HashSet<AddressAsKey> _destroyedThisRound = [];
    private readonly HashSet<StorageCell> _committedThisRound = [];

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

    public void SetBackendScope(IWorldStateScopeProvider.IScope scope) => _currentScope = scope;

    public override void Set(in StorageCell storageCell, byte[] newValue)
    {
        _metrics.IncrementStorageWrites();
        base.Set(in storageCell, newValue);
        // Populator executions never commit, so commit-time hints arrive too late.
        _currentScope.HintWarmSlot(new ValueAddress(storageCell.Address.Bytes), storageCell.Index);
    }

    /// <summary>
    /// Get the current value at the specified location
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <returns>Value at location</returns>
    protected override ReadOnlySpan<byte> GetCurrentValue(in StorageCell storageCell) =>
        TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes! : LoadFromTree(storageCell);

    /// <summary>
    /// Return the original persistent storage value from the storage cell
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    public ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
    {
        if (!_originalValues.TryGetValue(storageCell, out byte[] value))
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

    public bool IsStorageEmpty(Address address) => GetOrCreateStorage(address).IsEmpty;

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

        bool isTracing = tracer.IsTracingStorage;
        Dictionary<StorageCell, StorageChangeTrace>? trace = null;
        if (isTracing)
        {
            trace = [];
        }

        ReadOnlySpan<Change> changes = CollectionsMarshal.AsSpan(_changes);
        for (int i = 0; i <= currentPosition; i++)
        {
            ref readonly Change change = ref changes[currentPosition - i];
            if (!_committedThisRound.Add(change!.StorageCell))
            {
                continue;
            }

            // Debug-only: A broken index surfaces anyway as a storage-root mismatch on the block.
            Debug.Assert(_intraBlockCache[change.StorageCell].CurrentIdx == currentPosition - i,
                $"Expected the cached index to equal {currentPosition} - {i}");

            if (change.ChangeType == ChangeType.Update)
            {
                // A SaveChange would resurrect the dead value over the Clear() marker;
                // tracers still see the cell zeroed, as the journaled path reported it.
                if (_destroyedThisRound.Count != 0 && _destroyedThisRound.Contains(change.StorageCell.Address))
                {
                    if (isTracing)
                    {
                        trace![change.StorageCell] = new StorageChangeTrace(StorageTree.ZeroBytes);
                    }

                    continue;
                }

                if (_logger.IsTrace)
                {
                    _logger.Trace($"  Update {change.StorageCell.Address}_{change.StorageCell.Index} V = {change.Value.ToHexString(true)}");
                }

                if (_originalValues.TryGetValue(change.StorageCell, out byte[] initialValue) &&
                    initialValue.AsSpan().SequenceEqual(change.Value))
                {
                }
                else
                {
                    toUpdateRoots.Add(change.StorageCell.Address);

                    GetOrCreateStorage(change.StorageCell.Address)
                        .SaveChange(change.StorageCell, change.Value);
                }

                if (isTracing)
                {
                    trace![change.StorageCell] = new StorageChangeTrace(change.Value);
                }
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

        if (isTracing)
        {
            foreach ((StorageCell cell, byte[] originalValue) in _originalValues)
            {
                if (trace!.TryGetValue(cell, out StorageChangeTrace changeTrace))
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

        if (isTracing)
        {
            ReportChanges(tracer!, trace!);
        }
    }

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

            if (!_storages.TryGetValue(kvp.Key, out PerContractState contractState))
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

    public void ClearStorageMap()
    {
        _storages.Clear();
        InvalidateStorageMemo();
    }

    private Address? _lastStorageAddress;
    private PerContractState? _lastStorage;

    private void InvalidateStorageMemo()
    {
        _lastStorageAddress = null;
        _lastStorage = null;
    }

    private PerContractState GetOrCreateStorage(Address address)
    {
        if (_lastStorageAddress == address)
        {
            return _lastStorage!;
        }

        ref PerContractState? value = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (!exists) value = PerContractState.Rent(address, this);
        _lastStorageAddress = address;
        _lastStorage = value;
        return value;
    }

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
        _toUpdateRoots.TryAdd(address, true);
        GetOrCreateStorage(address).Clear();
    }

    public override void ClearStorage(Address address)
    {
        foreach (KeyValuePair<StorageCell, byte[]> readCell in _originalValues)
        {
            if (readCell.Key.Address == address)
            {
                Set(readCell.Key, StorageTree.ZeroBytes);
            }
        }

        base.ClearStorage(address);

        ResetContractState(address);
    }

    private sealed class DefaultableDictionary()
    {
        private bool _missingAreDefault;
        private readonly Dictionary<UInt256, StorageChangeTrace> _dictionary = new(Comparer.Instance);
        public int EstimatedSize => _dictionary.Count + (_missingAreDefault ? 1 : 0);
        public bool HasClear => _missingAreDefault;

        public void Reset(int capacity)
        {
            _missingAreDefault = false;
            _dictionary.ClearAndTrim(capacity, capacity);
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

            public bool Equals(UInt256 x, UInt256 y)
                => Unsafe.As<UInt256, Vector256<byte>>(ref x) == Unsafe.As<UInt256, Vector256<byte>>(ref y);

            public int GetHashCode([DisallowNull] UInt256 obj)
                => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in obj, 1)).FastHash();
        }

        public void UnmarkClear() => _missingAreDefault = false;
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
                // Self-destruct must be visible before commit; its deletion is not in the changelog.
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

            bool isEmpty = _backend.IsKnownEmpty;
            if (isEmpty && !_wasWritten)
            {
                // Mark all missing cells as zero to avoid tree lookups.
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
                _provider._metrics.IncrementStorageTreeCache();
            }

            uint round = _provider._originalsRound;
            if (valueChange.CapturedRound != round)
            {
                _provider.CaptureOriginalValue(storageCell, valueChange.After);
                valueChange = valueChange.WithCapturedRound(round);
            }

            return valueChange.After;
        }

        private byte[] LoadFromTreeStorage(StorageCell storageCell)
        {
            _provider._metrics.IncrementStorageTreeReads();

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

        public void RemoveStorageTree() => _backend = null;

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
