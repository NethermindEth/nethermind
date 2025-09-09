// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
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
    private readonly Dictionary<AddressAsKey, PerContractState> _storages = new(4_096);
    private readonly Dictionary<AddressAsKey, bool> _toUpdateRoots = new();

    /// <summary>
    /// EIP-1283
    /// </summary>
    private readonly Dictionary<StorageCell, byte[]> _originalValues = new();

    private readonly HashSet<StorageCell> _committedThisRound = new();
    private readonly ConcurrentDictionary<StorageCell, byte[]>? _preBlockCache;

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
                    trace![change.StorageCell] = new ChangeTrace(change.Value);
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
            foreach (KeyValuePair<AddressAsKey, PerContractState> kvp in _storages)
            {
                if (!_toUpdateRoots.TryGetValue(kvp.Key, out bool hasChanges) || !hasChanges)
                {
                    // Wasn't updated don't recalculate
                    continue;
                }

                PerContractState contractState = kvp.Value;
                (int writes, int skipped) = contractState.ProcessStorageChanges();
                ReportMetrics(writes, skipped);
                if (writes > 0)
                {
                    _stateProvider.UpdateStorageRoot(address: kvp.Key, contractState.RootHash);
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
                (storages, toUpdateRoots: _toUpdateRoots, writes: 0, skips: 0),
                static (i, state) =>
                {
                    ref var kvp = ref state.storages.GetRef(i);
                    if (!state.toUpdateRoots.TryGetValue(kvp.Key, out bool hasChanges) || !hasChanges)
                    {
                        // Wasn't updated don't recalculate
                        return state;
                    }

                    (int writes, int skipped) = kvp.Value.ProcessStorageChanges();
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

            // Update the storage roots in the main thread not in parallel,
            // as can't update the StateTrie in parallel.
            foreach (ref var kvp in storages.AsSpan())
            {
                if (!_toUpdateRoots.TryGetValue(kvp.Key, out bool hasChanges) || !hasChanges)
                {
                    continue;
                }

                // Update the storage root for the Account
                _stateProvider.UpdateStorageRoot(address: kvp.Key, kvp.Value.RootHash);
            }
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

    /// <summary>
    /// Commit persistent storage trees
    /// </summary>
    /// <param name="blockNumber">Current block number</param>
    public void CommitTrees(IBlockCommitter blockCommitter)
    {
        // Note: These all runs in about 0.4ms. So the little overhead like attempting to sort the tasks
        // may make it worst. Always check on mainnet.

        using ArrayPoolList<Task> commitTask = new ArrayPoolList<Task>(_storages.Count);
        foreach (KeyValuePair<AddressAsKey, PerContractState> storage in _storages)
        {
            storage.Value.EnsureStorageTree(); // Cannot be called concurrently
            if (blockCommitter.TryRequestConcurrencyQuota())
            {
                commitTask.Add(Task.Factory.StartNew((ctx) =>
                {
                    PerContractState st = (PerContractState)ctx;
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

    private PerContractState GetOrCreateStorage(Address address)
    {
        ref PerContractState? value = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (!exists) value = new PerContractState(address, this);
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
        return GetOrCreateStorage(storageCell.Address).LoadFromTree(storageCell);
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

        // here it is important to make sure that we will not reuse the same tree when the contract is revived
        // by means of CREATE 2 - notice that the cached trie may carry information about items that were not
        // touched in this block, hence were not zeroed above
        // TODO: how does it work with pruning?
        _toUpdateRoots.Remove(address);

        PerContractState state = GetOrCreateStorage(address);
        state.Clear();
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
        public int EstimatedSize => _dictionary.Count;

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

    private sealed class PerContractState
    {
        private StorageTree? StorageTree;
        private DefaultableDictionary BlockChange = new DefaultableDictionary();
        private bool _wasWritten = false;
        private readonly Func<StorageCell, byte[]> _loadFromTreeStorageFunc;
        private readonly Address _address;
        private readonly PersistentStorageProvider _provider;

        public PerContractState(Address address,
            PersistentStorageProvider provider)
        {
            _address = address;
            _provider = provider;
            _loadFromTreeStorageFunc = LoadFromTreeStorage;
        }

        public void EnsureStorageTree()
        {
            if (StorageTree is not null) return;

            // Note: GetStorageRoot is not concurrent safe! And so do this whole method!
            AccountStruct? acc = _provider._stateProvider.GetAccount(_address);
            ValueHash256 storageRoot = acc?.StorageRoot ?? Keccak.EmptyTreeHash;
            bool isEmpty = storageRoot == Keccak.EmptyTreeHash; // We know all lookups will be empty against this tree
            StorageTree = _provider._storageTreeFactory.Create(_address,
                _provider._trieStore.GetTrieStore(_address),
                storageRoot.ToCommitment(),
                _provider.StateRoot,
                _provider._logManager);

            if (isEmpty && !_wasWritten)
            {
                // Slight optimization that skips the tree
                BlockChange.ClearAndSetMissingAsDefault();
            }
        }

        public Hash256 RootHash
        {
            get
            {
                EnsureStorageTree();
                return StorageTree.RootHash;
            }
        }

        public void Commit()
        {
            EnsureStorageTree();
            StorageTree.Commit();
        }

        public void Clear()
        {
            StorageTree = new StorageTree(_provider._trieStore.GetTrieStore(_address), Keccak.EmptyTreeHash, _provider._logManager);
            BlockChange.ClearAndSetMissingAsDefault();
        }

        public void SaveChange(StorageCell storageCell, byte[] value)
        {
            _wasWritten = true;
            ref ChangeTrace valueChanges = ref BlockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
            if (!exists)
            {
                valueChanges = new ChangeTrace(value);
            }
            else
            {
                valueChanges.After = value;
            }
        }

        public ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell)
        {
            ref ChangeTrace valueChange = ref BlockChange.GetValueRefOrAddDefault(storageCell.Index, out bool exists);
            if (!exists)
            {
                byte[] value = !_provider._populatePreBlockCache ?
                    LoadFromTreeReadPreWarmCache(in storageCell) :
                    LoadFromTreePopulatePrewarmCache(in storageCell);

                valueChange = new(value, value);
            }
            else
            {
                Db.Metrics.IncrementStorageTreeCache();
            }

            if (!storageCell.IsHash) _provider.PushToRegistryOnly(storageCell, valueChange.After);
            return valueChange.After;
        }

        private byte[] LoadFromTreeReadPreWarmCache(in StorageCell storageCell)
        {
            if (_provider._preBlockCache?.TryGetValue(storageCell, out byte[] value) ?? false)
            {
                Db.Metrics.IncrementStorageTreeCache();
            }
            else
            {
                value = LoadFromTreeStorage(storageCell);
            }
            return value;
        }

        private byte[] LoadFromTreePopulatePrewarmCache(in StorageCell storageCell)
        {
            long priorReads = Db.Metrics.ThreadLocalStorageTreeReads;

            byte[] value = _provider._preBlockCache is not null
                ? _provider._preBlockCache.GetOrAdd(storageCell, _loadFromTreeStorageFunc)
                : LoadFromTreeStorage(storageCell);

            if (Db.Metrics.ThreadLocalStorageTreeReads == priorReads)
            {
                // Read from Concurrent Cache
                Db.Metrics.IncrementStorageTreeCache();
            }
            return value;
        }

        private byte[] LoadFromTreeStorage(StorageCell storageCell)
        {
            Db.Metrics.IncrementStorageTreeReads();

            EnsureStorageTree();
            return !storageCell.IsHash
                ? StorageTree.Get(storageCell.Index)
                : StorageTree.GetArray(storageCell.Hash.Bytes);
        }

        public (int writes, int skipped) ProcessStorageChanges()
        {
            EnsureStorageTree();

            int writes = 0;
            int skipped = 0;
            if (BlockChange.EstimatedSize < PatriciaTree.MinEntriesToParallelizeThreshold)
            {
                foreach (var kvp in BlockChange)
                {
                    byte[] after = kvp.Value.After;
                    if (!Bytes.AreEqual(kvp.Value.Before, after) || kvp.Value.IsInitialValue)
                    {
                        BlockChange[kvp.Key] = new(after, after);
                        StorageTree.Set(kvp.Key, after);
                        writes++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
            else
            {
                using ArrayPoolList<PatriciaTree.BulkSetEntry> bulkWrite = new(BlockChange.EstimatedSize);

                Span<byte> keyBuf = stackalloc byte[32];
                foreach (var kvp in BlockChange)
                {
                    byte[] after = kvp.Value.After;
                    if (!Bytes.AreEqual(kvp.Value.Before, after))
                    {
                        BlockChange[kvp.Key] = new(after, after);

                        StorageTree.ComputeKeyWithLookup(kvp.Key, keyBuf);
                        bulkWrite.Add(StorageTree.CreateBulkSetEntry(new ValueHash256(keyBuf), after));

                        writes++;
                    }
                    else
                    {
                        skipped++;
                    }
                }

                StorageTree.BulkSet(bulkWrite);
            }

            if (writes > 0)
            {
                StorageTree.UpdateRootHash(canBeParallel: writes > 64);
            }

            return (writes, skipped);
        }
    }
}
