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
using RuntimeInformation = Nethermind.Core.Cpu.RuntimeInformation;

namespace Nethermind.State;


/// <summary>
/// Manages persistent storage allowing for snapshotting and restoring
/// Persists data to ITrieStore
/// </summary>
internal sealed class PersistentStorageProvider : PartialStorageProviderBase
{
    private IWorldStateScopeProvider.IScope _currentScope = null!;
    private readonly StateProvider _stateProvider;
    private readonly Dictionary<AddressAsKey, PerContractState> _storages = new(4_096);
    private readonly Dictionary<AddressAsKey, bool> _toUpdateRoots = new();

    /// <summary>
    /// EIP-1283
    /// </summary>
    private readonly Dictionary<StorageCell, byte[]> _originalValues = new();

    private readonly HashSet<StorageCell> _committedThisRound = new();

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
    protected override ReadOnlySpan<byte> GetCurrentValue(in StorageCell storageCell) =>
        TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes! : LoadFromTree(storageCell);

    /// <summary>
    /// Return the original persistent storage value from the storage cell
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    public byte[] GetOriginal(in StorageCell storageCell)
    {
        if (!_originalValues.TryGetValue(storageCell, out var value))
        {
            throw new InvalidOperationException("Get original should only be called after get within the same caching round");
        }

        if (_transactionChangesSnapshots.TryPeek(out int snapshot))
        {
            if (_intraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
            {
                if (stack.TryGetSearchedItem(snapshot, out int lastChangeIndexBeforeOriginalSnapshot))
                {
                    return _changes[lastChangeIndexBeforeOriginalSnapshot].Value;
                }
            }
        }

        return value;
    }

    public Hash256 GetStorageRoot(Address address)
    {
        return GetOrCreateStorage(address).StorageRoot;
    }

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
            return;
        }
        if (_changes[currentPosition].IsNull)
        {
            throw new InvalidOperationException($"Change at current position {currentPosition} was null when committing {nameof(PartialStorageProviderBase)}");
        }

        HashSet<AddressAsKey> toUpdateRoots = (_tempToUpdateRoots ??= new());

        bool isTracing = tracer.IsTracingStorage;
        Dictionary<StorageCell, StorageChangeTrace>? trace = null;
        if (isTracing)
        {
            trace = [];
        }

        for (int i = 0; i <= currentPosition; i++)
        {
            Change change = _changes[currentPosition - i];
            if (!isTracing && change!.ChangeType == ChangeType.JustCache)
            {
                continue;
            }

            if (_committedThisRound.Contains(change!.StorageCell))
            {
                if (isTracing && change.ChangeType == ChangeType.JustCache)
                {
                    trace![change.StorageCell] = new StorageChangeTrace(change.Value, trace[change.StorageCell].After);
                }

                continue;
            }

            if (isTracing && change.ChangeType == ChangeType.JustCache)
            {
                tracer!.ReportStorageRead(change.StorageCell);
            }

            _committedThisRound.Add(change.StorageCell);

            if (change.ChangeType == ChangeType.Destroy)
            {
                continue;
            }

            int forAssertion = _intraBlockCache[change.StorageCell].Pop();
            if (forAssertion != currentPosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {currentPosition} - {i}");
            }

            if (change.ChangeType == ChangeType.Update)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"  Update {change.StorageCell.Address}_{change.StorageCell.Index} V = {change.Value.ToHexString(true)}");
                }

                if (_originalValues.TryGetValue(change.StorageCell, out byte[] initialValue) &&
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

                if (isTracing)
                {
                    trace![change.StorageCell] = new StorageChangeTrace(change.Value);
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
                    if (!state.toUpdateRoots.TryGetValue(kvp.Key, out bool hasChanges) || !hasChanges)
                    {
                        // Wasn't updated don't recalculate
                        return state;
                    }

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

    private ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell)
    {
        return GetOrCreateStorage(storageCell.Address).LoadFromTree(storageCell);
    }

    private void PushToRegistryOnly(in StorageCell cell, byte[] value)
    {
        StackList<int> stack = SetupRegistry(cell);
        _originalValues[cell] = value;
        stack.Push(_changes.Count);
        _changes.Add(new Change(in cell, value, ChangeType.JustCache));
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

        public void UnmarkClear()
        {
            _missingAreDefault = false;
        }
    }

    private sealed class PerContractState(Address address, PersistentStorageProvider provider) : IReturnable
    {
        private IWorldStateScopeProvider.IStorageTree? _backend;
        private readonly DefaultableDictionary _blockChange = new();
        private bool _wasWritten;
        private PersistentStorageProvider _provider = provider;
        private Address _address = address;

        private void Initialize(Address address, PersistentStorageProvider provider)
        {
            _address = address;
            _provider = provider;
        }

        public int EstimatedChanges => _blockChange.EstimatedSize;

        public Hash256 StorageRoot
        {
            get
            {
                EnsureStorageTree();
                return _backend!.RootHash;
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
                _blockChange.ClearAndSetMissingAsDefault();
            }
        }

        public void Clear()
        {
            EnsureStorageTree();
            _blockChange.ClearAndSetMissingAsDefault();
        }

        public void Return()
        {
            _address = null!;
            _provider = null!;
            _backend = null;
            _wasWritten = false;
            Pool.Return(this);
        }

        public void SaveChange(StorageCell storageCell, byte[] value)
        {
            _wasWritten = true;
            ref StorageChangeTrace valueChanges = ref _blockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
            valueChanges = !exists ? new StorageChangeTrace(value) : new StorageChangeTrace(valueChanges.Before, value);
        }

        public ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell)
        {
            ref StorageChangeTrace valueChange = ref _blockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
            if (!exists)
            {
                byte[] value = LoadFromTreeStorage(storageCell);
                valueChange = new(value, value);
            }
            else
            {
                Db.Metrics.IncrementStorageTreeCache();
            }

            if (!storageCell.IsHash) _provider.PushToRegistryOnly(storageCell, valueChange.After);
            return valueChange.After;
        }

        private byte[] LoadFromTreeStorage(StorageCell storageCell)
        {
            Db.Metrics.IncrementStorageTreeReads();

            EnsureStorageTree();
            return !storageCell.IsHash
                ? _backend!.Get(storageCell.Index)
                : _backend!.Get(storageCell.Hash);
        }

        private static byte[] LoadFromTreeStorage(StorageCell storageCell, PerContractState @this)
            => @this.LoadFromTreeStorage(storageCell);

        public (int writes, int skipped) ProcessStorageChanges(IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch)
        {
            EnsureStorageTree();

            using IWorldStateScopeProvider.IStorageWriteBatch _ = storageWriteBatch;

            int writes = 0;
            int skipped = 0;

            if (_blockChange.HasClear)
            {
                storageWriteBatch.Clear();
                _blockChange.UnmarkClear(); // Note: Until the storage write batch is disposed, this BlockCache will pass read through the uncleared storage tree
            }

            foreach (KeyValuePair<UInt256, StorageChangeTrace> kvp in _blockChange)
            {
                byte[] after = kvp.Value.After;
                if (!Bytes.AreEqual(kvp.Value.Before, after) || kvp.Value.IsInitialValue)
                {
                    _blockChange[kvp.Key] = new(after, after);
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

                if (item._blockChange.Capacity > MaxItemSize)
                    return;

                // shared pool fallback
                if (Interlocked.Increment(ref _poolCount) > MaxPooledCount)
                {
                    Interlocked.Decrement(ref _poolCount);
                    return;
                }

                item._blockChange.Reset();
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
