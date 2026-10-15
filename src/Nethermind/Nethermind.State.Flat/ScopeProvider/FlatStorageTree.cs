// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree(
    FlatWorldStateScope scope,
    ITrieWarmer trieCacheWarmer,
    SnapshotBundle bundle,
    IFlatDbConfig config,
    Hash256 storageRoot,
    Address address,
    ILogManager logManager) : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
{
    private readonly Address _address = address;
    private readonly IFlatDbConfig _config = config;
    private readonly ITrieWarmer _trieCacheWarmer = trieCacheWarmer;
    private readonly FlatWorldStateScope _scope = scope;
    private readonly SnapshotBundle _bundle = bundle;
    private readonly ILogManager _logManager = logManager;
    private readonly Hash256 _storageRoot = storageRoot;
    private Hash256? _addressHash;

    // Created on the owner thread by the first write batch or verified read; null in a trie-less scope.
    private LoggedTrie? _trie;
    // The root a trie created from now on starts at.
    private Hash256 _rootBeforeTrie = storageRoot;
    private StorageTree? _warmupTree;

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);

    private Hash256 AddressHash => _addressHash ??= _address.ToAccountPath.ToHash256();

    private LoggedTrie Trie => _trie ??= CreateTrie();

    private LoggedTrie CreateTrie()
    {
        TrieNodeLog log = _bundle.TrieNodeLog!;
        return new LoggedTrie(_bundle, log, AddressHash, LogBulkSet.ResolveRoot(log, AddressHash, _rootBeforeTrie.ValueHash256));
    }

    private StorageTree WarmupTree => Volatile.Read(ref _warmupTree) ?? CreateWarmupTree();

    private StorageTree CreateWarmupTree()
    {
        StorageTree warmup = new(new StorageTrieStoreWarmerAdapter(_bundle, AddressHash), _logManager);
        warmup.SetRootHash(_storageRoot, false);
        return Interlocked.CompareExchange(ref _warmupTree, warmup, null) ?? warmup;
    }

    public Hash256 RootHash => _trie?.RootHash ?? _rootBeforeTrie;

    internal bool IsDisposed => _scope.IsDisposed;

    public void Get(in UInt256 index, out UInt256 value)
    {
        _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx, out UInt256? slotValue);
        value = slotValue.GetValueOrDefault();

        // A trie-less (history-backed) scope has no storage trie to verify against — the reader throws on trie-node
        // access, and a historical value verified against the current trie would be wrong anyway.
        if (_config.VerifyWithTrie && !_scope.Trieless)
        {
            ValueHash256 key = default;
            StorageTree.ComputeKeyWithLookup(index, ref key);
            byte[]? encoded = Trie.TryGet(key);
            UInt256 treeValue = encoded is null ? default : new UInt256(new RlpReader(encoded).DecodeByteArraySpan(), isBigEndian: true);
            if (treeValue != value)
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {Trie.RootHash}, {index}. Tree: {treeValue} vs Flat: {value}. Self destruct it {_selfDestructKnownStateIdx}");
            }
        }
    }

    // Reads do not warm the trie: most reads come through the prewarmer, and read-only slots
    // (~30-40% of accesses per @weiihann's analysis) never need their trie path warmed because
    // they don't trigger commit-time tree updates. Warm-up is driven from HintSet on the write
    // path instead.
    public void HintSet(in UInt256 index) => WarmUpSlot(index);

    private void WarmUpSlot(UInt256 index)
    {
        if (_scope.WarmsTries && _bundle.ShouldQueuePrewarm(_address, index))
        {
            // ShouldQueuePrewarm already marked the slot in the dedupe bloom, so a rejected push loses the hint for good.
            _scope.IncrementOutstandingWarmups();
            if (!_trieCacheWarmer.PushSlotJob(this, index, _scope.HintSequenceId)
                && !_trieCacheWarmer.PushSlotJobMpmc(this, index, _scope.HintSequenceId))
                _scope.DecrementOutstandingWarmups();
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

            if (!_bundle.TryLeaseReadOnlyBundle())
            {
                return false;
            }

            try
            {
                // Note: storage tree root not changed after write batch. Also not cleared. So the result is not correct.
                // this is just to warm up the nodes.
                ValueHash256 key = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(index, ref key);

                WarmupTree.WarmUpPath(key.BytesAsSpan);
                return true;
            }
            finally
            {
                _bundle.ReleaseReadOnlyBundleLease();
            }
        }
        finally
        {
            _scope.DecrementOutstandingWarmups();
        }
    }

    private void Set(in UInt256 slot, in UInt256 value) => _bundle.SetChangedSlot(_address, slot, value);

    internal void ClearStorage()
    {
        _bundle.ClearStorage(_address, AddressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        // Trieless scopes too: IWorldState.GetStorageRoot still reads RootHash there.
        if (_trie is null) _rootBeforeTrie = Keccak.EmptyTreeHash;
        else _trie.Clear();
    }

    public void CommitTree() => _trie?.Publish();

    /// <summary>Whether the storage trie holds nodes written since its last commit.</summary>
    internal bool HasUncommittedNodes => _trie is { HasUnpublishedNodes: true };

    /// <summary>Releases the trie's buffers once the scope no longer uses this tree.</summary>
    internal void ReleaseTrie() => _trie?.Dispose();

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated) =>
        // A trie-less (history-backed) scope can't maintain the storage trie (its persistence reader throws on
        // trie-node access), so it writes only the flat overlay. Pick the strategy once here.
        _scope.Trieless
            ? new FlatOverlayStorageWriteBatch(this)
            : new LoggedStorageWriteBatch(this, Trie, estimatedEntries, onRootUpdated);

    // Normal scope: maintain the storage trie (for the root) and mirror values into the flat overlay.
    private sealed class LoggedStorageWriteBatch(
        FlatStorageTree storageTree,
        LoggedTrie trie,
        int estimatedEntries,
        Action<Address, Hash256> onRootUpdated) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private readonly ArrayPoolList<PatriciaTree.BulkSetEntry> _entries = new(estimatedEntries);
        private bool _hasSelfDestruct;

        [SkipLocalsInit]
        public void Set(in UInt256 index, in UInt256 value)
        {
            Unsafe.SkipInit(out EvmWord word);
            bool isZero = value.IsZero;
            ValueHash256 key = default;
            StorageTree.ComputeKeyWithLookup(index, ref key);
            _entries.Add(StorageTree.CreateBulkSetEntry(key, isZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref word), isZero));
            storageTree.Set(index, value);
        }

        public void Clear()
        {
            if (_entries.Count > 0) throw new InvalidOperationException("Must call clear first in a storage write batch");
            _hasSelfDestruct = true;
            storageTree.ClearStorage();
        }

        public void Dispose()
        {
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = _entries.ToRef();
            if (entries.Count > 0) trie.BulkSet(entries, canParallelize: true);
            // Deferred, the scope commit publishes the nodes after the block is reported valid.
            if (!storageTree._config.DeferStorageTrieCommit) storageTree.CommitTree();
            if (entries.Count > 0 || _hasSelfDestruct) onRootUpdated(storageTree._address, storageTree.RootHash);
        }
    }

    // Trie-less scope: only the flat overlay is written; there is no storage trie to maintain.
    private sealed class FlatOverlayStorageWriteBatch(FlatStorageTree storageTree) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, in UInt256 value) => storageTree.Set(index, value);

        public void Clear() => storageTree.ClearStorage();

        public void Dispose() { }
    }
}
