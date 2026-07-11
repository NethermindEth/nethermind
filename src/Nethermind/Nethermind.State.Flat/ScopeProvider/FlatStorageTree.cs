// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
{
    private readonly StorageTree _tree;
    // Only constructed when the warmer actually walks it (Legacy variant). Other variants
    // (None â†’ noop warmer; SparseProof â†’ direct proof reads) never touch this field.
    private readonly StorageTree? _warmupStorageTree;
    private readonly Address _address;
    private readonly IFlatDbConfig _config;
    private readonly ITrieWarmer _trieCacheWarmer;
    private readonly FlatWorldStateScope _scope;
    private readonly SnapshotBundle _bundle;
    private readonly Hash256 _addressHash;

    /// <summary>Exposed for sparse trie proof prefetching (M5).</summary>
    public Hash256 AccountPathHash => _addressHash;

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    private int _selfDestructKnownStateIdx;

    public FlatStorageTree(
        FlatWorldStateScope scope,
        ITrieWarmer trieCacheWarmer,
        SnapshotBundle bundle,
        IFlatDbConfig config,
        ConcurrencyController concurrencyQuota,
        Hash256 storageRoot,
        Address address,
        ILogManager logManager)
    {
        _scope = scope;
        _trieCacheWarmer = trieCacheWarmer;
        _bundle = bundle;
        _address = address;
        _addressHash = address.ToAccountPath.ToHash256();
        _selfDestructKnownStateIdx = bundle.DetermineSelfDestructSnapshotIdx(address);

        StorageTrieStoreAdapter storageTrieAdapter = new(bundle, concurrencyQuota, _addressHash);

        _tree = new StorageTree(storageTrieAdapter, storageRoot, logManager)
        {
            RootHash = storageRoot
        };

        // Warmup tree is only needed for the Legacy walker. SparseProof/None never call
        // WarmUpStorageTrie, so allocating one per contract per scope is pure waste.
        if (config.SparseTrieWarmer == SparseTrieWarmerVariant.Legacy
            && config.TrieWarmerWorkerCount != 0)
        {
            StorageTrieStoreWarmerAdapter warmerStorageTrieAdapter = new(bundle, _addressHash);
            _warmupStorageTree = new StorageTree(warmerStorageTrieAdapter, logManager);
            _warmupStorageTree.SetRootHash(storageRoot, false);
            // Share the live tree's RootRef so the warmup walker skips a top-level lookup.
            _warmupStorageTree.RootRef = _tree.RootRef;
        }

        _config = config;
    }

    public Hash256 RootHash => _tree.RootHash;

    internal bool IsDisposed => _scope.IsDisposed;

    public byte[] Get(in UInt256 index)
    {
        byte[]? value = _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx);
        if (value is null || value.Length == 0)
        {
            value = StorageTree.ZeroBytes;
        }

        if (_config.VerifyWithTrie)
        {
            byte[] treeValue = _tree.Get(index);
            if (!Bytes.AreEqual(treeValue, value))
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {_tree.RootHash}, {index}. Tree: {treeValue?.ToHexString()} vs Flat: {value?.ToHexString()}. Self destruct it {_selfDestructKnownStateIdx}");
            }
        }

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
            // ShouldQueuePrewarm already marked the slot in the dedupe bloom, so a rejected push loses the hint for good.
            if (_trieCacheWarmer.PushSlotJob(this, index, _scope.HintSequenceId)
                || _trieCacheWarmer.PushSlotJobMpmc(this, index, _scope.HintSequenceId))
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

            if (!_bundle.TryLeaseReadOnlyBundle())
            {
                return false;
            }

            try
            {
                // Flat-native warmer: warm the actual read path (SnapshotBundle -> persistence ->
                // RocksDB) the EVM will hit at commit, with no Patricia decode. This is the read
                // GetSlot performs, so it primes the RocksDB block cache + flat lookup cheaply.
                if (_config.SparseTrieWarmer == SparseTrieWarmerVariant.Flat)
                {
                    _ = _bundle.GetSlot(_address, index, _selfDestructKnownStateIdx);
                    return true;
                }

                ValueHash256 key = ValueKeccak.Zero;
                StorageTree.ComputeKeyWithLookup(index, ref key);

                if (_config.SparseTrieWarmer == SparseTrieWarmerVariant.SparseProof
                    && _scope.SparseProofReader is not null && _tree.RootHash != Keccak.EmptyTreeHash)
                {
                    // EXPERIMENTAL â€” mirrors the account-side warmer's SparseProof variant. Full
                    // root-to-leaf storage proof read with the result discarded; the decoded proof
                    // is never revealed into the sparse storage trie. Only DB/OS page-cache warming
                    // until the M5 background sparse task can consume these reads. See the matching
                    // comment in FlatWorldStateScope.WarmUpStateTrie.
                    _ = Nethermind.Trie.Sparse.MultiProofReader.ReadStorageProofs(
                        _scope.SparseProofReader, _addressHash, _tree.RootHash, [key.ToCommitment()]);
                }
                else if (_warmupStorageTree is not null)
                {
                    // Note: storage tree root not changed after write batch. Also not cleared. So the result is not correct.
                    // this is just to warm up the nodes.
                    _warmupStorageTree.WarmUpPath(key.BytesAsSpan);
                }
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

    private void Set(UInt256 slot, byte[] value) => _bundle.SetChangedSlot(_address, slot, value);

    public void SelfDestruct()
    {
        _bundle.Clear(_address, _addressHash);
        _selfDestructKnownStateIdx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        _tree.RootHash = Keccak.EmptyTreeHash;
    }

    public void CommitTree() => _tree.Commit();

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        // M3: when sparse storage is authoritative, bypass Patricia storage tree work
        // entirely. The sparse computer computes per-contract storage roots; the resulting
        // root is fed back via onRootUpdated so the account RLP picks it up.
        if (_scope.UseSparseStorageRoot)
        {
            return new SparseStorageWriteBatch(this, _scope.SparseRootComputerInternal!, onRootUpdated, estimatedEntries);
        }

        TrieStoreScopeProvider.StorageTreeBulkWriteBatch storageTreeBulkWriteBatch = new(
                estimatedEntries,
                _tree,
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

    /// <summary>
    /// Sparse-only storage write batch. Replaces <see cref="TrieStoreScopeProvider.StorageTreeBulkWriteBatch"/>
    /// when the sparse trie is authoritative for storage. Collects per-slot changes into a hashed
    /// dictionary, feeds them to <see cref="SparseRootComputer.AddStorageChanges"/>, and computes
    /// the new storage root on Dispose. The Patricia storage tree is not touched.
    /// </summary>
    private sealed class SparseStorageWriteBatch(
        FlatStorageTree storageTree,
        SparseRootComputer sparseRootComputer,
        Action<Address, Hash256> onRootUpdated,
        int estimatedEntries) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private readonly FlatStorageTree _storageTree = storageTree;
        private readonly SparseRootComputer _sparseRootComputer = sparseRootComputer;
        private readonly Action<Address, Hash256> _onRootUpdated = onRootUpdated;
        private readonly Hash256 _previousStorageRoot = storageTree._tree.RootHash;
        // Keyed by ValueHash256 (struct) so the per-slot key avoids the Hash256 (class)
        // allocation that ToCommitment used to produce. On storage-heavy blocks the slot
        // keccak â†’ Hash256 path was the single largest GC contributor; using the struct
        // key directly eliminates that alloc.
        private readonly Dictionary<ValueHash256, LeafUpdate> _slotUpdates = new(estimatedEntries);
        // Diagnostic: capture raw (slot, value) pairs to drive a parallel Patricia
        // computation when SparseTrieShadowStorageCompare is on.
        private readonly List<(UInt256 Index, byte[] Value)>? _rawUpdates
            = storageTree._config.SparseTrieShadowStorageCompare ? new(estimatedEntries) : null;
        private bool _hasSelfDestruct;
        private bool _wasSetCalled;

        public void Set(in UInt256 index, byte[] value)
        {
            _wasSetCalled = true;
            ValueHash256 slotKey = default;
            StorageTree.ComputeKeyWithLookup(index, ref slotKey);

            // Patricia's StorageTree.CreateBulkSetEntry: zero/null → empty (delete);
            // otherwise → RLP-encoded bytes (storage leaf value). Sparse must store the
            // same encoded form, otherwise the storage root differs and the account RLP
            // ends up pointing at a wrong subtree.
            if (value.IsZero())
            {
                _slotUpdates[slotKey] = LeafUpdate.Deleted();
            }
            else
            {
                Nethermind.Serialization.Rlp.Rlp rlpEncoded = Nethermind.Serialization.Rlp.Rlp.Encode(value);
                byte[]? encodedBytes = rlpEncoded?.Bytes;
                if (encodedBytes is null || encodedBytes.Length == 0)
                {
                    _slotUpdates[slotKey] = LeafUpdate.Deleted();
                }
                else
                {
                    _slotUpdates[slotKey] = LeafUpdate.Changed(encodedBytes);
                }
            }

            // Keep the SnapshotBundle in sync for storage READS during this block.
            _storageTree.Set(index, value!);

            _rawUpdates?.Add((index, value));
        }

        public void Clear()
        {
            if (_wasSetCalled) throw new InvalidOperationException("Must call Clear before any Set in a storage write batch");
            _hasSelfDestruct = true;
            _storageTree.SelfDestruct();
        }

        public void Dispose()
        {
            if (!_wasSetCalled && !_hasSelfDestruct) return;

            // PersistentStorageProvider.UpdateRootHashesMultiThread runs this in parallel
            // across contracts. Safety relies on:
            //   1. SparseRootComputer._storageChanges and SparseStateTrie._storageTries
            //      are ConcurrentDictionary — insertion/lookup race-free.
            //   2. Each per-contract SparsePatriciaTree is touched by exactly one batch
            //      (this one), so its arena mutation has a single writer.
            //   3. MultiProofReader is stateless; ITrieNodeReader is documented thread-safe.

            // Patricia semantics: Clear may be followed by Sets in the same batch (self-destruct
            // then redeploy). When _hasSelfDestruct is set, wipe the sparse storage trie first
            // so subsequent Sets land in an empty trie rooted at EmptyTreeHash.
            Hash256 effectivePrevRoot = _previousStorageRoot;
            if (_hasSelfDestruct)
            {
                _sparseRootComputer.Trie.WipeStorage(_storageTree._addressHash);
                effectivePrevRoot = Keccak.EmptyTreeHash;
            }

            if (!_wasSetCalled)
            {
                _storageTree._tree.SetRootHash(Keccak.EmptyTreeHash, resetObjects: false);
                _onRootUpdated(_storageTree._address, Keccak.EmptyTreeHash);
                return;
            }

            // Cross-block state corruption check: BEFORE applying this block's updates, the
            // sparse storage trie's current root must equal _previousStorageRoot. If it doesn't,
            // some prior block left the in-memory trie in a state inconsistent with what was
            // persisted / what the account RLP says — pointing the bug at cross-block reuse,
            // not at this block's compute.
            if (_storageTree._config.SparseTrieShadowStorageCompare && !_hasSelfDestruct)
            {
                Hash256 trieCurrentRoot = _sparseRootComputer.Trie.ComputeStorageRoot(_storageTree._addressHash);
                if (trieCurrentRoot != _previousStorageRoot)
                {
                    ILogger logger = _storageTree._scope._logManager.GetClassLogger<SparseStorageWriteBatch>();
                    if (logger.IsWarn) logger.Warn(
                        $"SPARSE STORAGE CROSS-BLOCK STATE DRIFT! Address={_storageTree._address}, " +
                        $"ExpectedPrevRoot={_previousStorageRoot}, SparseInMemoryRoot={trieCurrentRoot}");
                }
            }

            _sparseRootComputer.AddStorageChanges(_storageTree._addressHash, effectivePrevRoot, _slotUpdates);
            Hash256 newRoot = _sparseRootComputer.ComputeStorageRoot(_storageTree._addressHash);

            // Diagnostic shadow comparison: drive Patricia with the same raw inputs and
            // log the first divergence. Patricia tree is still at the pre-block state
            // (we skip BulkSet in authoritative-storage mode), so we can mutate it safely
            // here purely for diagnostic purposes.
            if (_rawUpdates is not null)
            {
                ShadowComparePatricia(newRoot);
            }

            // Keep FlatStorageTree.RootHash in sync so subsequent same-block reads via Get
            // (and the eventual account encoding) see the post-batch root.
            // Use SetRootHash(false) to skip RootRef re-lookup. The property setter calls
            // SetRootHash(true), which hits StorageTrieStoreAdapter.FindCachedOrUnknown and
            // throws NodeHashMismatchException when the transient cache has a stale entry
            // at (address, EmptyPath). In sparse-authoritative mode Patricia's RootRef is
            // unused so we don't need to keep it in sync.
            _storageTree._tree.SetRootHash(newRoot, resetObjects: false);
            _onRootUpdated(_storageTree._address, newRoot);
        }

        private void ShadowComparePatricia(Hash256 sparseRoot)
        {
            try
            {
                // Reset Patricia tree to the pre-block state. SetRootHash(false) is safe
                // (no DB lookup), then BulkSet applies the raw updates.
                _storageTree._tree.SetRootHash(_previousStorageRoot, resetObjects: false);

                // 'using' so a throw between Add and ToRef/BulkSet doesn't leak the pooled
                // array. The previous manual Dispose() at the end of the block ran only on
                // the success path; an exception during BulkSet/UpdateRootHash left the
                // ArrayPool entry permanently retained until process exit.
                using ArrayPoolList<PatriciaTree.BulkSetEntry> bulkEntries = new(_rawUpdates!.Count);
                ValueHash256 keyBuf = default;
                foreach ((UInt256 index, byte[] value) in _rawUpdates)
                {
                    StorageTree.ComputeKeyWithLookup(index, ref keyBuf);
                    bulkEntries.Add(StorageTree.CreateBulkSetEntry(keyBuf, value));
                }

                if (_hasSelfDestruct)
                {
                    _storageTree._tree.SetRootHash(Keccak.EmptyTreeHash, resetObjects: false);
                }
                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> asRef = bulkEntries.ToRef();
                _storageTree._tree.BulkSet(asRef);
                _storageTree._tree.UpdateRootHash(_rawUpdates.Count > 64);
                Hash256 patriciaRoot = _storageTree._tree.RootHash;

                if (patriciaRoot != sparseRoot)
                {
                    ILogger logger = _storageTree._scope._logManager.GetClassLogger<SparseStorageWriteBatch>();
                    if (logger.IsWarn)
                    {
                        logger.Warn(
                            $"SPARSE STORAGE DIVERGENCE! Address={_storageTree._address}, " +
                            $"AddressHash={_storageTree._addressHash}, PrevRoot={_previousStorageRoot}, " +
                            $"SparseRoot={sparseRoot}, PatriciaRoot={patriciaRoot}, " +
                            $"SlotCount={_rawUpdates.Count}, HasSelfDestruct={_hasSelfDestruct}");

                        // Dump every (slot, value) so we can write a focused TDD reproducer.
                        System.Text.StringBuilder dump = new();
                        dump.Append("SPARSE STORAGE DIVERGENCE UPDATES: ");
                        foreach ((UInt256 idx, byte[] val) in _rawUpdates)
                        {
                            dump.Append(idx.ToString());
                            dump.Append("=0x");
                            dump.Append(Convert.ToHexString(val));
                            dump.Append(';');
                        }
                        logger.Warn(dump.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                ILogger logger = _storageTree._scope._logManager.GetClassLogger<SparseStorageWriteBatch>();
                if (logger.IsWarn) logger.Warn(
                    $"Shadow Patricia comparison threw for address {_storageTree._address}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
