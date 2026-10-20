// SPDX-FileCopyrightText: 2025-2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatStorageTree(
    FlatWorldStateScope scope,
    ITrieWarmer trieCacheWarmer,
    SnapshotBundle bundle,
    IFlatDbConfig config,
    Hash256 storageRoot,
    Address address) : IWorldStateScopeProvider.IStorageTree, ITrieWarmer.IStorageWarmer
{
    private readonly Address _address = address;
    private readonly IFlatDbConfig _config = config;
    private readonly ITrieWarmer _trieCacheWarmer = trieCacheWarmer;
    private readonly FlatWorldStateScope _scope = scope;
    private readonly SnapshotBundle _bundle = bundle;
    private readonly Hash256 _storageRoot = storageRoot;
    private Hash256? _addressHash;
    // Whether the bundle has this address registered for its slot writes. The scope commit that drops every
    // registration also drops its trees, so writes after it reach a new tree, and only ClearStorage resets this.
    private bool _changedSlotsRegistered;

    // Created on the owner thread by the first write batch or verified read; null in a trie-less scope.
    private NodeRecordTrie? _trie;
    // The root a trie created from now on starts at.
    private Hash256 _rootBeforeTrie = storageRoot;
    // The root warm-ups walk from, resolved by the first one on a warmer thread.
    private StrongBox<(ValueHash256 Hash, ulong Pointer)>? _preBlockRoot;

    private const int EarlyIdle = 0;
    private const int EarlyApplying = 1;
    private const int EarlyClaimed = 2;
    private const int EarlyAbandoned = 3;

    // Committed writes waiting for the early apply thread, in commit order.
    private ConcurrentQueue<(UInt256 Slot, UInt256 Value)>? _earlyWrites;
    // Owned by whoever holds _earlyState.
    private SnapshotBundle.BackgroundTrieSource? _earlySource;
    private (ValueHash256 Hash, ulong Pointer) _earlyRoot;
    private Dictionary<UInt256, UInt256>? _earlyApplied;
    private int _earlyState;
    private int _earlyQueued;
    private readonly int _earlyGeneration = scope.EarlyApplyGeneration;

    private const int SelfDestructIdxUnknown = int.MinValue;

    // This number is the idx of the snapshot in the SnapshotBundle where a clear for this account was found.
    // This is passed to TryGetSlot which prevent it from reading before self destruct.
    // Found on the first read, as trees that only warm the trie never read.
    private int _selfDestructKnownStateIdx = SelfDestructIdxUnknown;

    private int SelfDestructKnownStateIdx
    {
        get
        {
            int idx = Volatile.Read(ref _selfDestructKnownStateIdx);
            return idx != SelfDestructIdxUnknown ? idx : FindSelfDestructKnownStateIdx();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int FindSelfDestructKnownStateIdx()
    {
        int idx = _bundle.DetermineSelfDestructSnapshotIdx(_address);
        // A ClearStorage that set it meanwhile is newer than what this read found.
        int current = Interlocked.CompareExchange(ref _selfDestructKnownStateIdx, idx, SelfDestructIdxUnknown);
        return current == SelfDestructIdxUnknown ? idx : current;
    }

    private Hash256 AddressHash => _addressHash ??= _address.ToAccountPath.ToHash256();

    private NodeRecordTrie GetTrie() => _trie ??= CreateTrie();

    private NodeRecordTrie CreateTrie()
    {
        INodeRecordBuffer buffer = _bundle.NodeRecordBuffer!;
        return new NodeRecordTrie(_bundle, AddressHash, buffer.ResolveRoot(AddressHash, _rootBeforeTrie.ValueHash256));
    }

    private (ValueHash256 Hash, ulong Pointer) PreBlockRoot => (Volatile.Read(ref _preBlockRoot) ?? ResolvePreBlockRoot()).Value;

    private StrongBox<(ValueHash256 Hash, ulong Pointer)> ResolvePreBlockRoot()
    {
        StrongBox<(ValueHash256 Hash, ulong Pointer)> resolved = new(_bundle.NodeRecordBuffer!.ResolveRoot(AddressHash, _storageRoot.ValueHash256));
        Volatile.Write(ref _preBlockRoot, resolved);
        return resolved;
    }

    public Hash256 RootHash => _trie?.RootHash ?? _rootBeforeTrie;

    internal bool IsDisposed => _scope.IsDisposed;

    public void Get(in UInt256 index, out UInt256 value)
    {
        int selfDestructKnownStateIdx = SelfDestructKnownStateIdx;
        _bundle.GetSlot(_address, index, selfDestructKnownStateIdx, out UInt256? slotValue);
        value = slotValue.GetValueOrDefault();

        // A trie-less (history-backed) scope has no storage trie to verify against — the reader throws on trie-node
        // access, and a historical value verified against the current trie would be wrong anyway.
        if (_config.VerifyWithTrie && !_scope.Trieless)
        {
            ValueHash256 key = default;
            StorageTree.ComputeKeyWithLookup(index, ref key);
            byte[]? encoded = GetTrie().TryGet(key);
            UInt256 treeValue = encoded is null ? default : new UInt256(new RlpReader(encoded).DecodeByteArraySpan(), isBigEndian: true);
            if (treeValue != value)
            {
                throw new TrieException($"Get slot got wrong value. Address {_address}, {GetTrie().RootHash}, {index}. Tree: {treeValue} vs Flat: {value}. Self destruct it {selfDestructKnownStateIdx}");
            }
        }
    }

    // Reads do not warm the trie: most reads come through the prewarmer, and read-only slots
    // (~30-40% of accesses per @weiihann's analysis) never need their trie path warmed because
    // they don't trigger commit-time tree updates. Warm-up is driven from HintSet on the write
    // path instead.
    public void HintSet(in UInt256 index) => WarmUpSlot(index, isDelete: false);

    /// <inheritdoc/>
    public void HintSet(in UInt256 index, in UInt256 value)
    {
        WarmUpSlot(index, isDelete: value.IsZero);
        if (!_scope.AppliesStorageWritesEarly || Volatile.Read(ref _earlyState) >= EarlyClaimed) return;

        ConcurrentQueue<(UInt256 Slot, UInt256 Value)> writes = _earlyWrites ?? CreateEarlyWrites();
        writes.Enqueue((index, value));
        // Clearing this before draining makes a hint during a pass queue another pass.
        if (Volatile.Read(ref _earlyQueued) == 0 && Interlocked.Exchange(ref _earlyQueued, 1) == 0) _scope.EarlyApplier.Enqueue(this);
    }

    private ConcurrentQueue<(UInt256 Slot, UInt256 Value)> CreateEarlyWrites()
    {
        // Created on the owning thread, which can still read the mutable bundle the source and pre-block root come from.
        _earlySource = _bundle.CreateBackgroundTrieSource(AddressHash);
        try
        {
            _earlyRoot = _bundle.NodeRecordBuffer!.ResolveRoot(AddressHash, _rootBeforeTrie.ValueHash256);
        }
        catch
        {
            _earlySource.Dispose();
            throw;
        }
        return _earlyWrites = new();
    }

    [SkipLocalsInit]
    internal void ApplyEarlyWrites()
    {
        Interlocked.Exchange(ref _earlyQueued, 0);
        if (_scope.EarlyApplyClosed || _scope.EarlyApplyGeneration != _earlyGeneration
            || Interlocked.CompareExchange(ref _earlyState, EarlyApplying, EarlyIdle) != EarlyIdle) return;

        ConcurrentQueue<(UInt256 Slot, UInt256 Value)> writes = _earlyWrites!;
        try
        {
            Dictionary<UInt256, UInt256> applied = _earlyApplied ??= [];
            Dictionary<UInt256, UInt256> latest = new(writes.Count);
            while (writes.TryDequeue(out (UInt256 Slot, UInt256 Value) write)) latest[write.Slot] = write.Value;
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(latest.Count);
            Unsafe.SkipInit(out EvmWord word);
            ValueHash256 key = default;
            foreach ((UInt256 slot, UInt256 value) in latest)
            {
                if (applied.TryGetValue(slot, out UInt256 current) && current == value) continue;
                bool isZero = value.IsZero;
                StorageTree.ComputeKeyWithLookup(slot, ref key);
                entries.Add(StorageTree.CreateBulkSetEntry(key, isZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref word), isZero));
                applied[slot] = value;
            }

            if (entries.Count > 0)
            {
                OnEarlyPassDrained?.Invoke();
                // Pool work would run at normal priority rather than this thread's idle priority.
                _earlyRoot = NodeRecordBulkSet.BulkSet(_earlySource!, AddressHash, _earlyRoot, entries, canParallelize: false);
                _scope.CountEarlyApplied(entries.Count);
            }
        }
        catch
        {
            Volatile.Write(ref _earlyState, EarlyAbandoned);
            throw;
        }
        finally
        {
            // Keep the disposed source as a tombstone: an accepted hint may still arrive after a failed pass.
            if (Interlocked.CompareExchange(ref _earlyState, EarlyIdle, EarlyApplying) != EarlyApplying) _earlySource!.Dispose();
        }
    }

    /// <summary>Whether every queued write has been applied to the early tree. For tests.</summary>
    internal bool EarlyWritesDrained => Volatile.Read(ref _earlyWrites) is not { IsEmpty: false }
        && Volatile.Read(ref _earlyState) != EarlyApplying && Volatile.Read(ref _earlyQueued) == 0;

    /// <summary>Called once a pass has taken its writes. For tests.</summary>
    internal Action? OnEarlyPassDrained;

    private Dictionary<UInt256, UInt256>? AdoptEarlyTrie(NodeRecordTrie trie)
    {
        int previous = ClaimEarlyTrie();
        if (previous == EarlyApplying)
        {
            _scope.CountEarlyAbandoned();
            return null;
        }

        if (previous != EarlyIdle) return null;
        using SnapshotBundle.BackgroundTrieSource? source = Interlocked.Exchange(ref _earlySource, null);
        if (_earlyApplied is not { Count: > 0 } applied) return null;
        trie.Adopt(_earlyRoot);
        return applied;
    }

    private void AbandonEarlyTrie()
    {
        if (ClaimEarlyTrie() == EarlyIdle) Interlocked.Exchange(ref _earlySource, null)?.Dispose();
    }

    private int ClaimEarlyTrie()
    {
        while (true)
        {
            int state = Volatile.Read(ref _earlyState);
            if (state >= EarlyClaimed) return state;
            int claimed = state == EarlyApplying ? EarlyAbandoned : EarlyClaimed;
            if (Interlocked.CompareExchange(ref _earlyState, claimed, state) == state) return state;
        }
    }

    private void WarmUpSlot(UInt256 index, bool isDelete)
    {
        // A delete skips the dedupe: the slot's path was most likely queued already, by the prewarmer or by an
        // earlier write, but only a delete needs the branch sibling its leaf leaves behind.
        if (_scope.WarmsTries && (isDelete || _bundle.ShouldQueuePrewarm(_address, index)))
        {
            // For a non-delete, ShouldQueuePrewarm already marked the slot in the dedupe bloom, so a rejected push loses the hint for good.
            _scope.IncrementOutstandingWarmups();
            if (!_trieCacheWarmer.PushSlotJob(this, index, _scope.HintSequenceId, isDelete)
                && !_trieCacheWarmer.PushSlotJobMpmc(this, index, _scope.HintSequenceId, isDelete))
                _scope.DecrementOutstandingWarmups();
        }
    }

    // Called by trie warmer.
    public bool WarmUpStorageTrie(UInt256 index, int sequenceId, bool isDelete)
    {
        try
        {
            if (_scope.HintSequenceId != sequenceId || _scope._pausePrewarmer || _scope.Trieless)
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

                _bundle.WarmUp(AddressHash, PreBlockRoot, key, forDelete: isDelete);
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

    private void Set(in UInt256 slot, in UInt256 value)
    {
        // The address is the same for every slot of the tree, so it is registered once instead of hashed per slot.
        if (!_changedSlotsRegistered)
        {
            _bundle.RegisterAddressWithChangedSlots(_address);
            _changedSlotsRegistered = true;
        }

        _bundle.SetChangedSlotValue(_address, slot, value);
    }

    internal void ClearStorage()
    {
        AbandonEarlyTrie();
        _bundle.ClearStorage(_address, AddressHash);
        _changedSlotsRegistered = false;
        Volatile.Write(ref _selfDestructKnownStateIdx, _bundle.DetermineSelfDestructSnapshotIdx(_address));
        // Trieless scopes too: IWorldState.GetStorageRoot still reads RootHash there.
        if (_trie is null) _rootBeforeTrie = Keccak.EmptyTreeHash;
        else _trie.Clear();
    }

    public void CommitTree() => _trie?.Publish();

    /// <summary>Whether the storage trie holds nodes written since its last commit.</summary>
    internal bool HasUncommittedNodes => _trie is { HasUnpublishedNodes: true };

    /// <summary>Abandons private early updates once the scope no longer uses this tree.</summary>
    internal void ReleaseTrie() => AbandonEarlyTrie();

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);
        NodeRecordTrie trie = GetTrie();
        return new BufferedStorageWriteBatch(this, trie, estimatedEntries, onRootUpdated, AdoptEarlyTrie(trie));
    }

    // Normal scope: maintain the storage trie (for the root) and mirror values into the flat overlay.
    private sealed class BufferedStorageWriteBatch(
        FlatStorageTree storageTree,
        NodeRecordTrie trie,
        int estimatedEntries,
        Action<Address, Hash256> onRootUpdated,
        Dictionary<UInt256, UInt256>? earlyApplied) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private readonly ArrayPoolList<PatriciaTree.BulkSetEntry> _entries = new(estimatedEntries);
        private bool _hasSelfDestruct;
        private Dictionary<UInt256, UInt256>? _earlyApplied = earlyApplied;
        private int _reused;

        [SkipLocalsInit]
        public void Set(in UInt256 index, in UInt256 value)
        {
            storageTree.Set(index, value);
            if (_earlyApplied is not null && _earlyApplied.Remove(index, out UInt256 applied) && applied == value)
            {
                _reused++;
                return;
            }

            AddEntry(index, value);
        }

        [SkipLocalsInit]
        private void AddEntry(in UInt256 index, in UInt256 value)
        {
            Unsafe.SkipInit(out EvmWord word);
            bool isZero = value.IsZero;
            ValueHash256 key = default;
            StorageTree.ComputeKeyWithLookup(index, ref key);
            _entries.Add(StorageTree.CreateBulkSetEntry(key, isZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref word), isZero));
        }

        public void Clear()
        {
            if (_entries.Count > 0 || _reused > 0) throw new InvalidOperationException("Must call clear first in a storage write batch");
            _hasSelfDestruct = true;
            _earlyApplied = null;
            storageTree.ClearStorage();
        }

        public void Dispose()
        {
            int restored = 0;
            if (_earlyApplied is { Count: > 0 } leftovers)
            {
                foreach (UInt256 slot in leftovers.Keys)
                {
                    storageTree.Get(slot, out UInt256 value);
                    AddEntry(slot, value);
                    restored++;
                }
            }

            storageTree._scope.CountEarlyReconciled(_reused, restored);
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = _entries.ToRef();
            if (entries.Count > 0) trie.BulkSet(entries, canParallelize: true);
            // Deferred, the scope commit publishes the nodes after the block is reported valid.
            if (!storageTree._config.DeferStorageTrieCommit) storageTree.CommitTree();
            if (entries.Count > 0 || _reused > 0 || _hasSelfDestruct) onRootUpdated(storageTree._address, storageTree.RootHash);
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
