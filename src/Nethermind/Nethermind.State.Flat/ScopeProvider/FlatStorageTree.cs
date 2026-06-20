// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree(
    FlatWorldStateScope scope,
    ITrieWarmer trieCacheWarmer,
    SnapshotBundle bundle,
    IFlatDbConfig config,
    ConcurrencyController concurrencyQuota,
    Hash256 storageRoot,
    Address address,
    PreservedStorageTries? preservedStorageTries,
    ILogManager logManager) : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
{
    private StorageTree? _tree;
    private StorageTree? _warmupStorageTree;
    private readonly Address _address = address;
    private readonly IFlatDbConfig _config = config;
    private readonly ITrieWarmer _trieCacheWarmer = trieCacheWarmer;
    private readonly FlatWorldStateScope _scope = scope;
    private readonly SnapshotBundle _bundle = bundle;
    private readonly ConcurrencyController _concurrencyQuota = concurrencyQuota;
    private readonly Hash256 _addressHash = address.ToAccountPath.ToHash256();
    private Hash256 _storageRoot = storageRoot;
    private UInt256 _lastIndex;
    private byte[]? _lastValue;
    private bool _hasLastValue;
    private readonly PreservedStorageTries? _preservedStorageTries = preservedStorageTries;
    private PreservedStorageTries.Rebinder? _storageTreeRebinder;
    private readonly ILogManager _logManager = logManager;

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);

    public Hash256 RootHash => _tree?.RootHash ?? _storageRoot;

    internal bool IsDisposed => _scope.IsDisposed;

    public byte[] Get(in UInt256 index)
    {
        if (!_config.VerifyWithTrie && _hasLastValue && _lastIndex.Equals(index))
        {
            return _lastValue!;
        }

        byte[]? value = _bundle.GetSlot(_address, _addressHash.ValueHash256, index, _selfDestructKnownStateIdx);
        if (value is null || value.Length == 0)
        {
            value = StorageTree.ZeroBytes;
        }

        if (_config.VerifyWithTrie)
        {
            byte[] treeValue = EnsureStorageTree().Get(index);
            if (!Bytes.AreEqual(treeValue, value))
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {RootHash}, {index}. Tree: {treeValue?.ToHexString()} vs Flat: {value?.ToHexString()}. Self destruct it {_selfDestructKnownStateIdx}");
            }
        }

        _lastIndex = index;
        _lastValue = value;
        _hasLastValue = true;
        return value!;
    }

    // Reads do not warm the trie: most reads come through the prewarmer, and read-only slots
    // (~30-40% of accesses per @weiihann's analysis) never need their trie path warmed because
    // they don't trigger commit-time tree updates. Warm-up is driven from HintSet on the write
    // path instead.
    public void HintSet(in UInt256 index, byte[]? value) => WarmUpSlot(index);

    private void WarmUpSlot(UInt256 index)
    {
        if (_bundle.ShouldQueuePrewarm(_address, index))
        {
            if (_trieCacheWarmer.PushSlotJob(this, index, _scope.HintSequenceId))
                _scope.IncrementOutstandingWarmups();
        }
    }

    // Called by trie warmer.
    public bool WarmUpStorageTrie(UInt256 index, int sequenceId)
    {
        try
        {
            if (_scope.HintSequenceId != sequenceId || _scope._pausePrewarmer)
            {
                return false;
            }

            // Note: storage tree root not changed after write batch. Also not cleared. So the result is not correct.
            // this is just to warm up the nodes.
            ValueHash256 key = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(index, ref key);

            EnsureWarmupStorageTree().WarmUpPath(key.BytesAsSpan);
            return true;
        }
        finally
        {
            _scope.DecrementOutstandingWarmups();
        }
    }

    public byte[] Get(in ValueHash256 hash) => throw new NotSupportedException("Not supported");

    private void Set(UInt256 slot, byte[] value)
    {
        _bundle.SetChangedSlot(_address, slot, value);

        _lastIndex = slot;
        _lastValue = value.Length == 0 ? StorageTree.ZeroBytes : value;
        _hasLastValue = true;
    }

    public void SelfDestruct()
    {
        _bundle.Clear(_address, _addressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        _storageRoot = Keccak.EmptyTreeHash;
        if (_tree is not null) _tree.RootHash = Keccak.EmptyTreeHash;
        _hasLastValue = false;
        _lastValue = null;
    }

    public void CommitTree() => _tree?.Commit();

    public void Preserve()
    {
        if (_preservedStorageTries is null || _tree is null || _storageTreeRebinder is null) return;
        _preservedStorageTries.Store(_address, _tree, _storageTreeRebinder, _tree.RootHash);
    }

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch storageTreeBulkWriteBatch = new(
                estimatedEntries,
                EnsureStorageTree(),
                onRootUpdated,
                _address,
                commit: true);

        return new StorageTreeBulkWriteBatch(
            storageTreeBulkWriteBatch,
            this
        );
    }

    private class StorageTreeBulkWriteBatch(
        TrieStoreScopeProvider.StorageTreeBulkWriteBatch storageTreeBulkWriteBatch,
        FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            storageTreeBulkWriteBatch.Set(in index, value);
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            storageTreeBulkWriteBatch.Clear();
            storageTree.SelfDestruct();
        }

        public void Dispose() => storageTreeBulkWriteBatch.Dispose();
    }

    private StorageTree EnsureStorageTree()
    {
        if (_tree is not null) return _tree;

        StorageTrieStoreAdapter storageTrieAdapter = new(_bundle, _concurrencyQuota, _addressHash);
        if (_preservedStorageTries is not null
            && _preservedStorageTries.TryTake(_address, _storageRoot, _bundle, _concurrencyQuota, out StorageTree reusedTree, out PreservedStorageTries.Rebinder rebinder))
        {
            _tree = reusedTree;
            _storageTreeRebinder = rebinder;
        }
        else
        {
            _tree = new StorageTree(storageTrieAdapter, _storageRoot, _logManager)
            {
                RootHash = _storageRoot
            };
            _storageTreeRebinder = _preservedStorageTries is not null ? storageTrieAdapter.Rebind : null;
        }

        return _tree;
    }

    private StorageTree EnsureWarmupStorageTree()
    {
        if (_warmupStorageTree is not null) return _warmupStorageTree;

        StorageTree tree = EnsureStorageTree();
        _warmupStorageTree = new StorageTree(new StorageTrieStoreWarmerAdapter(_bundle, _addressHash), _logManager);
        _warmupStorageTree.SetRootHash(tree.RootHash, false);
        _warmupStorageTree.RootRef = tree.RootRef;
        return _warmupStorageTree;
    }
}
