// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

using Nethermind.Core.Cpu;

/// <summary>
/// Manages persistent storage allowing for snapshotting and restoring
/// Persists data to ITrieStore
/// </summary>
internal sealed class PersistentStorageProvider : PartialStorageProviderBase
{
    private readonly ITrieStore _trieStore;
    private readonly StateProvider _stateProvider;
    private readonly ILogManager? _logManager;
    internal readonly IStorageTreeFactory _storageTreeFactory;
    private readonly Dictionary<AddressAsKey, StorageTree> _storages = new();
    private readonly HashSet<AddressAsKey> _toUpdateRoots = new();

    /// <summary>
    /// EIP-1283
    /// </summary>
    private readonly Dictionary<StorageCell, byte[]> _originalValues = new();

    private readonly HashSet<StorageCell> _committedThisRound = new();
    private readonly Dictionary<AddressAsKey, DefaultableDictionary> _blockChanges = new(4_096);
    private readonly ConcurrentDictionary<StorageCell, byte[]>? _preBlockCache;
    private readonly Func<StorageCell, byte[]> _loadFromTree;

    /// <summary>
    /// Manages persistent storage allowing for snapshotting and restoring
    /// Persists data to ITrieStore
    /// </summary>
    public PersistentStorageProvider(ITrieStore trieStore,
        StateProvider stateProvider,
        ILogManager logManager,
        IStorageTreeFactory? storageTreeFactory,
        ConcurrentDictionary<StorageCell, byte[]>? preBlockCache,
        bool populatePreBlockCache) : base(logManager)
    {
        _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _storageTreeFactory = storageTreeFactory ?? new StorageTreeFactory();
        _preBlockCache = preBlockCache;
        _populatePreBlockCache = populatePreBlockCache;
        _loadFromTree = LoadFromTreeStorage;
    }

    public Hash256 StateRoot { get; set; } = null!;
    private readonly bool _populatePreBlockCache;

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
            _blockChanges.Clear();
            _storages.Clear();
            _toUpdateRoots.Clear();
        }
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
        Dictionary<StorageCell, ChangeTrace>? trace = null;
        if (isTracing)
        {
            trace = new Dictionary<StorageCell, ChangeTrace>();
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
                    trace![change.StorageCell] = new ChangeTrace(change.Value, trace[change.StorageCell].After);
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

                SaveChange(toUpdateRoots, change);

                if (isTracing)
                {
                    trace![change.StorageCell] = new ChangeTrace(change.Value);
                }
            }
        }

        foreach (AddressAsKey address in toUpdateRoots)
        {
            // since the accounts could be empty accounts that are removing (EIP-158)
            if (_stateProvider.AccountExists(address))
            {
                _toUpdateRoots.Add(address);
                // Add storage tree, will accessed later, which may be in parallel
                // As we can't add a new storage tries in parallel to the _storages Dict do it here
                GetOrCreateStorage(address, out _);
            }
            else
            {
                _toUpdateRoots.Remove(address);
                _storages.Remove(address);
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

    protected override void CommitStorageRoots()
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
            foreach (KeyValuePair<AddressAsKey, StorageTree> kvp in _storages)
            {
                if (!_toUpdateRoots.Contains(kvp.Key))
                {
                    // Wasn't updated don't recalculate
                    continue;
                }

                StorageTree storageTree = kvp.Value;
                DefaultableDictionary dict = _blockChanges[kvp.Key];
                (int writes, int skipped) = ProcessStorageChanges(dict, storageTree);
                ReportMetrics(writes, skipped);
                if (writes > 0)
                {
                    _stateProvider.UpdateStorageRoot(address: kvp.Key, storageTree.RootHash);
                }
            }
        }

        void UpdateRootHashesMultiThread()
        {
            // We can recalculate the roots in parallel as they are all independent tries
            using var storages = _storages.ToPooledList();
            ParallelUnbalancedWork.For(
                0,
                storages.Count,
                RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                (storages, toUpdateRoots: _toUpdateRoots, changes: _blockChanges, writes: 0, skips: 0),
                static (i, state) =>
            {
                ref var kvp = ref state.storages.GetRef(i);
                if (!state.toUpdateRoots.Contains(kvp.Key))
                {
                    // Wasn't updated don't recalculate
                    return state;
                }

                StorageTree storageTree = kvp.Value;
                DefaultableDictionary dict = state.changes[kvp.Key];
                (int writes, int skipped) = ProcessStorageChanges(dict, storageTree);
                if (writes == 0)
                {
                    lock (state.toUpdateRoots)
                    {
                        // No changes to this storage, remove from toUpdateRoots
                        // Needs to be under lock as regular HashSet, should be
                        // uncommon enough not to cause contention.
                        state.toUpdateRoots.Remove(kvp.Key);
                    }
                }
                else
                {
                    state.writes += writes;
                }

                state.skips += skipped;

                return state;
            },
            (state) => ReportMetrics(state.writes, state.skips));

            // Update the storage roots in the main thread not in parallel,
            // as can't update the StateTrie in parallel.
            foreach (ref var kvp in storages.AsSpan())
            {
                if (!_toUpdateRoots.Contains(kvp.Key))
                {
                    continue;
                }

                // Update the storage root for the Account
                _stateProvider.UpdateStorageRoot(address: kvp.Key, kvp.Value.RootHash);
            }
        }

        static (int writes, int skipped) ProcessStorageChanges(DefaultableDictionary dict, StorageTree storageTree)
        {
            int writes = 0;
            int skipped = 0;
            foreach (var kvp in dict)
            {
                byte[] after = kvp.Value.After;
                if (!Bytes.AreEqual(kvp.Value.Before, after))
                {
                    dict[kvp.Key] = new(after, after);
                    storageTree.Set(kvp.Key, after);
                    writes++;
                }
                else
                {
                    skipped++;
                }
            }

            if (writes > 0)
            {
                storageTree.UpdateRootHash(canBeParallel: true);
            }

            return (writes, skipped);
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

    private void SaveChange(HashSet<AddressAsKey> toUpdateRoots, Change change)
    {
        if (_originalValues.TryGetValue(change.StorageCell, out byte[] initialValue) &&
            initialValue.AsSpan().SequenceEqual(change.Value))
        {
            // no need to update the tree if the value is the same
            return;
        }

        toUpdateRoots.Add(change.StorageCell.Address);

        ref DefaultableDictionary? dict = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockChanges, change.StorageCell.Address, out bool exists);
        if (!exists)
        {
            dict = new DefaultableDictionary();
        }

        ref ChangeTrace valueChanges = ref dict.GetValueRefOrAddDefault(change.StorageCell.Index, out exists);
        if (!exists)
        {
            valueChanges = new ChangeTrace(change.Value);
        }
        else
        {
            valueChanges.After = change.Value;
        }
    }

    /// <summary>
    /// Commit persistent storage trees
    /// </summary>
    /// <param name="blockNumber">Current block number</param>
    public void CommitTrees(IBlockCommitter blockCommitter)
    {
        // Note: These all runs in about 0.4ms. So the little overhead like attempting to sort the tasks
        // may make it worst. Always check on mainnet.

        using ArrayPoolList<Task> commitTask = new ArrayPoolList<Task>(_storages.Count);
        foreach (KeyValuePair<AddressAsKey, StorageTree> storage in _storages)
        {
            if (blockCommitter.TryRequestConcurrencyQuota())
            {
                commitTask.Add(Task.Factory.StartNew((ctx) =>
                {
                    StorageTree st = (StorageTree)ctx;
                    st.Commit();
                    blockCommitter.ReturnConcurrencyQuota();
                }, storage.Value));
            }
            else
            {
                storage.Value.Commit();
            }
        }

        Task.WaitAll(commitTask.AsSpan());

        _storages.Clear();
    }

    private StorageTree GetOrCreateStorage(Address address, out bool isEmpty)
    {
        isEmpty = false;
        ref StorageTree? value = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (!exists)
        {
            Hash256 storageRoot = _stateProvider.GetStorageRoot(address);
            isEmpty = storageRoot == Keccak.EmptyTreeHash; // We know all lookups will be empty against this tree
            value = _storageTreeFactory.Create(address, _trieStore.GetTrieStore(address), storageRoot, StateRoot, _logManager);
        }

        return value;
    }

    public void WarmUp(in StorageCell storageCell, bool isEmpty)
    {
        if (isEmpty)
        {
            if (_preBlockCache is not null)
            {
                _preBlockCache[storageCell] = [];
            }
        }
        else
        {
            LoadFromTree(in storageCell);
        }
    }

    private ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell)
    {
        ref DefaultableDictionary? dict = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockChanges, storageCell.Address, out bool exists);
        if (!exists)
        {
            dict = new DefaultableDictionary();
        }

        ref ChangeTrace valueChange = ref dict.GetValueRefOrAddDefault(storageCell.Index, out exists);
        if (!exists)
        {
            byte[] value = !_populatePreBlockCache ?
                LoadFromTreeReadPreWarmCache(in storageCell) :
                LoadFromTreePopulatePrewarmCache(in storageCell);

            valueChange = new(value, value);
        }
        else
        {
            Db.Metrics.IncrementStorageTreeCache();
        }

        if (!storageCell.IsHash) PushToRegistryOnly(storageCell, valueChange.After);
        return valueChange.After;
    }

    private byte[] LoadFromTreePopulatePrewarmCache(in StorageCell storageCell)
    {
        long priorReads = Db.Metrics.ThreadLocalStorageTreeReads;

        byte[] value = _preBlockCache is not null
            ? _preBlockCache.GetOrAdd(storageCell, _loadFromTree)
            : _loadFromTree(storageCell);

        if (Db.Metrics.ThreadLocalStorageTreeReads == priorReads)
        {
            // Read from Concurrent Cache
            Db.Metrics.IncrementStorageTreeCache();
        }
        return value;
    }

    private byte[] LoadFromTreeReadPreWarmCache(in StorageCell storageCell)
    {
        if (_preBlockCache?.TryGetValue(storageCell, out byte[] value) ?? false)
        {
            Db.Metrics.IncrementStorageTreeCache();
        }
        else
        {
            value = _loadFromTree(storageCell);
        }
        return value;
    }

    private byte[] LoadFromTreeStorage(StorageCell storageCell)
    {
        StorageTree tree = GetOrCreateStorage(storageCell.Address, out bool isEmpty);
        if (isEmpty)
        {
            // We know all lookups will be empty against this tree
            _blockChanges[storageCell.Address].ClearAndSetMissingAsDefault();
            return StorageTree.ZeroBytes;
        }

        Db.Metrics.IncrementStorageTreeReads();
        return !storageCell.IsHash ? tree.Get(storageCell.Index) : tree.GetArray(storageCell.Hash.Bytes);
    }

    private void PushToRegistryOnly(in StorageCell cell, byte[] value)
    {
        StackList<int> stack = SetupRegistry(cell);
        _originalValues[cell] = value;
        stack.Push(_changes.Count);
        _changes.Add(new Change(in cell, value, ChangeType.JustCache));
    }

    private static void ReportChanges(IStorageTracer tracer, Dictionary<StorageCell, ChangeTrace> trace)
    {
        foreach ((StorageCell address, ChangeTrace change) in trace)
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

        ref DefaultableDictionary? dict = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockChanges, address, out bool exists);
        if (!exists)
        {
            dict = new DefaultableDictionary();
        }

        // We know all lookups will be empty against this tree
        dict.ClearAndSetMissingAsDefault();

        // here it is important to make sure that we will not reuse the same tree when the contract is revived
        // by means of CREATE 2 - notice that the cached trie may carry information about items that were not
        // touched in this block, hence were not zeroed above
        // TODO: how does it work with pruning?
        _toUpdateRoots.Remove(address);
        _storages[address] = new StorageTree(_trieStore.GetTrieStore(address), Keccak.EmptyTreeHash, _logManager);
    }

    private class StorageTreeFactory : IStorageTreeFactory
    {
        public StorageTree Create(Address address, IScopedTrieStore trieStore, Hash256 storageRoot, Hash256 stateRoot, ILogManager? logManager)
            => new(trieStore, storageRoot, logManager);
    }

    private sealed class DefaultableDictionary()
    {
        private bool _missingAreDefault;
        private readonly Dictionary<UInt256, ChangeTrace> _dictionary = new(Comparer.Instance);

        public void ClearAndSetMissingAsDefault()
        {
            _missingAreDefault = true;
            _dictionary.Clear();
        }

        public ref ChangeTrace GetValueRefOrAddDefault(UInt256 storageCellIndex, out bool exists)
        {
            ref ChangeTrace value = ref CollectionsMarshal.GetValueRefOrAddDefault(_dictionary, storageCellIndex, out exists);
            if (!exists && _missingAreDefault)
            {
                // Where we know the rest of the tree is empty
                // we can say the value was found but is default
                // rather than having to check the database
                value = ChangeTrace.ZeroBytes;
                exists = true;
            }
            return ref value;
        }

        public ref ChangeTrace GetValueRefOrNullRef(UInt256 storageCellIndex)
            => ref CollectionsMarshal.GetValueRefOrNullRef(_dictionary, storageCellIndex);

        public ChangeTrace this[UInt256 key]
        {
            set => _dictionary[key] = value;
        }

        public Dictionary<UInt256, ChangeTrace>.Enumerator GetEnumerator() => _dictionary.GetEnumerator();

        private sealed class Comparer : IEqualityComparer<UInt256>
        {
            public static Comparer Instance { get; } = new();

            private Comparer() { }

            public bool Equals(UInt256 x, UInt256 y)
                => Unsafe.As<UInt256, Vector256<byte>>(ref x) == Unsafe.As<UInt256, Vector256<byte>>(ref y);

            public int GetHashCode([DisallowNull] UInt256 obj)
                => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in obj, 1)).FastHash();
        }
    }
}
