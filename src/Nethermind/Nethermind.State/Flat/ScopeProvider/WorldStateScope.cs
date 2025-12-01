// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State.Flat.ScopeProvider;

public class WorldStateScope : IWorldStateScopeProvider.IScope
{
    private StateId _currentStateId;
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IWorldStateScopeProvider.ICodeDb _codeDb;
    private readonly IFlatDiffRepository _flatDiffRepository;
    private readonly Dictionary<AddressPrefixAsKey, StorageTree> _storages = new();
    private readonly StateTree _stateTree;
    private readonly PatriciaTree _warmupStateTree;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private FlatDiffRepository.Configuration _configuration;

    public static Histogram _flatScopeTimer = Metrics.CreateHistogram("flat_scope_timer", "timer",
        new HistogramConfiguration()
        {
            LabelNames = ["part", "is_main"],
            Buckets = Histogram.PowersOfTenDividedBuckets(4, 10, 5)
        });

    private Histogram.Child _stateTreeGet;
    private Histogram.Child _flatGet;
    private readonly ConcurrencyQuota _concurrencyQuota;
    private readonly ITrieStoreTrieCacheWarmer _warmer;
    private bool _isCommitting = false;
    private readonly bool _isMain;
    private readonly Histogram.Child _storageCommit;
    private readonly Histogram.Child _stateCommit;
    private readonly Histogram.Child _snapshotCollect;
    private readonly Histogram.Child _snapshotAdd;

    public WorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatDiffRepository flatDiffRepository,
        FlatDiffRepository.Configuration configuration,
        ITrieStoreTrieCacheWarmer trieCacheWarmer,
        ILogManager logManager,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        _codeDb = codeDb;
        _flatDiffRepository = flatDiffRepository;
        _concurrencyQuota = new ConcurrencyQuota();
        _stateTree = new StateTree(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota, isReadOnly: false),
            logManager
        );
        _stateTree.RootHash = currentStateId.stateRoot.ToCommitment();
        _warmupStateTree = new PatriciaTree(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota, isReadOnly: true),
            logManager
        );
        _warmupStateTree.RootHash = currentStateId.stateRoot.ToCommitment();
        _configuration = configuration;
        _logManager = logManager;
        _warmer = trieCacheWarmer;
        _warmer.OnNewScope();
        _isReadOnly = isReadOnly;
        _isMain = !isReadOnly;

        _stateTreeGet = _flatScopeTimer.WithLabels("statetree_get", _isMain.ToString());
        _flatGet = _flatScopeTimer.WithLabels("flat_get", _isMain.ToString());
        _storageCommit = _flatScopeTimer.WithLabels("storage_commit", _isMain.ToString());
        _stateCommit = _flatScopeTimer.WithLabels("state_commit", _isMain.ToString());
        _snapshotCollect = _flatScopeTimer.WithLabels("snapshot_collect", _isMain.ToString());
        _snapshotAdd = _flatScopeTimer.WithLabels("snapshot_add", _isMain.ToString());
    }

    public void Dispose()
    {
        _snapshotBundle.Dispose();
    }

    public Hash256 RootHash => _stateTree.RootHash;
    public void UpdateRootHash()
    {
        _stateTree.UpdateRootHash();
    }

    public Account? Get(Address address)
    {
        long sw = Stopwatch.GetTimestamp();
        if (!_configuration.ReadWithTrie && _snapshotBundle.TryGetAccount(address, out Account account))
        {
            _flatGet.Observe(Stopwatch.GetTimestamp() - sw);
            HintGet(address, account);

            if (_configuration.VerifyWithTrie)
            {
                // TODO: To snapshot bundler
                sw = Stopwatch.GetTimestamp();
                Account? accTrie = _stateTree.Get(address);
                if (accTrie != account)
                {
                    throw new Exception($"Incorrect account {accTrie} vs {account}");
                }
                _stateTreeGet.Observe(Stopwatch.GetTimestamp() - sw);
            }

            return account;
        }
        else
        {
            account = _stateTree.Get(address);
            HintGet(address, account);
            _stateTreeGet.Observe(Stopwatch.GetTimestamp() - sw);
            return account;
        }
    }

    public void HintGet(Address address, Account? account)
    {
        if (_snapshotBundle.HintAccountRead(address, account))
        {
            _warmer.PushJob(this, address, null, null);
        }
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;
    public bool ShouldStillWarmUpTrie => !_isCommitting;

    public void WarmUpStateTrie(Address address)
    {
        ValueHash256 rawHash = address.ToAccountPath;
        _warmupStateTree.Get(rawHash.Bytes);
    }

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
    {
        return CreateStorageTreeImpl(address);
    }

    private StorageTree CreateStorageTreeImpl(Address address)
    {
        ref StorageTree storage = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (exists)
        {
            return storage;
        }

        Hash256 storageRoot = Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        storage = new StorageTree(
            this,
            _warmer,
            _snapshotBundle.GatherStorageCache(address),
            _configuration,
            _concurrencyQuota,
            storageRoot,
            address,
            _logManager);

        return storage;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(long blockNumber)
    {
        StateId newStateId = new StateId(blockNumber, RootHash);
        _isCommitting = true;
        long sw = Stopwatch.GetTimestamp();
        if (!_isReadOnly)
        {

            // Commit will copy the trie nodes from the tree to the bundle.
            using ArrayPoolList<Task> commitTask = new ArrayPoolList<Task>(_storages.Count);

            foreach (KeyValuePair<AddressPrefixAsKey, StorageTree> storage in _storages)
            {
                if (_concurrencyQuota.TryRequestConcurrencyQuota())
                {
                    commitTask.Add(Task.Factory.StartNew((ctx) =>
                    {
                        StorageTree st = (StorageTree)ctx;
                        st.CommitTree();
                        _concurrencyQuota.ReturnConcurrencyQuota();
                    }, storage.Value));
                }
                else
                {
                    storage.Value.CommitTree();
                }
            }

            Task.WaitAll(commitTask.AsSpan());

            _storageCommit.Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            _stateTree.Commit();

            _stateCommit.Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();
        }

        _isCommitting = false;

        _storages.Clear();

        Snapshot newSnapshot = _snapshotBundle.CollectAndApplyKnownState(_currentStateId, newStateId);

        _snapshotCollect.Observe(Stopwatch.GetTimestamp() - sw);
        sw = Stopwatch.GetTimestamp();

        if (!_isReadOnly)
        {
            if (_currentStateId != newStateId) _flatDiffRepository.AddSnapshot(newSnapshot);
            _snapshotAdd.Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();
        }

        _currentStateId = newStateId;
    }

    private class StateTrieStoreAdapter(
        SnapshotBundle bundle,
        ConcurrencyQuota concurrencyQuota,
        bool isReadOnly
    ) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            if (bundle.TryFindNode(path, hash, out var node))
            {
                return node;
            }

            TrieNode newNode = new TrieNode(NodeType.Unknown, hash);
            if (!isReadOnly) bundle.SetStateNode(path, newNode);
            return newNode;
        }

        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => bundle.TryLoadRlp(path, hash, flags);

        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(bundle, concurrencyQuota);

        private class Committer(SnapshotBundle bundle, ConcurrencyQuota concurrencyQuota) : AbstractMinimalCommitter(concurrencyQuota)
        {
            public override TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                bundle.SetStateNode(path, node);
                return node;
            }
        }
    }

    private class WriteBatch(
        WorldStateScope scope,
        int estimatedAccountCount,
        ILogger logger
    ) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressPrefixAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressPrefixAsKey, Hash256)> _dirtyStorageTree = new();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account)
        {
            _dirtyAccounts[key] = account;
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries)
        {
            return scope
                .CreateStorageTreeImpl(address)
                .CreateWriteBatch(
                    estimatedEntries: estimatedEntries,
                    onRootUpdated: (address, newRoot) => MarkDirty(address, newRoot));
        }

        private void MarkDirty(AddressPrefixAsKey address, Hash256 storageTreeRootHash)
        {
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));
        }

        public void Dispose()
        {
            while (_dirtyStorageTree.TryDequeue(out var entry))
            {
                (AddressPrefixAsKey key, Hash256 storageRoot) = entry;
                if (!_dirtyAccounts.TryGetValue(key, out var account)) account = scope.Get(key);
                if (account == null && storageRoot == Keccak.EmptyTreeHash) continue;
                account ??= ThrowNullAccount(key);
                account = account!.WithChangedStorageRoot(storageRoot);
                _dirtyAccounts[key] = account;
                OnAccountUpdated?.Invoke(key, new IWorldStateScopeProvider.AccountUpdated(key, account));
                if (logger.IsTrace) Trace(key, storageRoot, account);
            }

            using (var stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count))
            {
                foreach (var kv in _dirtyAccounts)
                {
                    stateSetter.Set(kv.Key, kv.Value);
                }
            }
            scope._snapshotBundle.ApplyStateChanges(_dirtyAccounts);


            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account)
                => logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");

            [DoesNotReturn, StackTraceHidden]
            static Account ThrowNullAccount(Address address)
                => throw new InvalidOperationException($"Account {address} is null when updating storage hash");
        }
    }
}
