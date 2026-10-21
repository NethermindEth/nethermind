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
using Nethermind.Trie;
using Nethermind.Trie.Native;

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

    private NativeTrieNodeStore? _nativeStore;
    private Hash256 _nativeRoot = storageRoot;

    private const int EarlyIdle = 0;
    private const int EarlyApplying = 1;
    private const int EarlyClaimed = 2;
    private const int EarlyAbandoned = 3;

    private EarlyTrie? _earlyTrie;
    private int _earlyState;
    private int _earlyQueued;
    private readonly int _earlyGeneration = scope.EarlyApplyGeneration;

    private sealed class EarlyTrie(SnapshotBundle bundle, Hash256 address, Hash256 root) : IDisposable
    {
        public readonly SnapshotBundle.BackgroundNativeTrieSource NativeSource = bundle.CreateBackgroundNativeTrieSource(address);
        public readonly ConcurrentQueue<(UInt256 Slot, UInt256 Value)> Writes = new();
        public readonly Dictionary<UInt256, UInt256> Applied = [];
        public readonly Hash256 InitialRoot = root;
        public NativeBulkSetState? NativeState;

        public void Dispose()
        {
            NativeState?.Dispose();
            NativeSource.Dispose();
        }
    }

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

    // Warmers read it concurrently with the owner, which must not lose its pending nodes to a store a warmer created.
    private NativeTrieNodeStore NativeStore =>
        Volatile.Read(ref _nativeStore)
        ?? Interlocked.CompareExchange(ref _nativeStore, new NativeTrieNodeStore(_bundle, AddressHash, _scope.NodeRecordBuffer), null)
        ?? _nativeStore!;

    public Hash256 RootHash => _nativeRoot;

    internal bool IsDisposed => _scope.IsDisposed;

    public void Get(in UInt256 index, out UInt256 value)
    {
        _bundle.GetSlot(_address, index, SelfDestructKnownStateIdx, out UInt256? slotValue);
        value = slotValue.GetValueOrDefault();
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

        OnEarlyHintAccepted?.Invoke();
        EarlyTrie early = _earlyTrie ??= new EarlyTrie(_bundle, AddressHash, RootHash);
        early.Writes.Enqueue((index, value));
        // Clearing this before draining makes a hint during a pass queue another pass.
        if (Volatile.Read(ref _earlyQueued) == 0 && Interlocked.Exchange(ref _earlyQueued, 1) == 0) _scope.EarlyApplier.Enqueue(this);
    }

    [SkipLocalsInit]
    internal void ApplyEarlyWrites()
    {
        Interlocked.Exchange(ref _earlyQueued, 0);
        if (_scope.EarlyApplyClosed || _scope.EarlyApplyGeneration != _earlyGeneration
            || Interlocked.CompareExchange(ref _earlyState, EarlyApplying, EarlyIdle) != EarlyIdle) return;

        EarlyTrie early = _earlyTrie!;
        try
        {
            Dictionary<UInt256, UInt256> latest = new(early.Writes.Count);
            while (early.Writes.TryDequeue(out (UInt256 Slot, UInt256 Value) write)) latest[write.Slot] = write.Value;
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(latest.Count);
            Unsafe.SkipInit(out EvmWord word);
            ValueHash256 key = default;
            foreach ((UInt256 slot, UInt256 value) in latest)
            {
                if (early.Applied.TryGetValue(slot, out UInt256 applied) && applied == value) continue;
                bool isZero = value.IsZero;
                StorageTree.ComputeKeyWithLookup(slot, ref key);
                entries.Add(StorageTree.CreateBulkSetEntry(key, isZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref word), isZero));
                early.Applied[slot] = value;
            }

            if (entries.Count > 0)
            {
                OnEarlyPassDrained?.Invoke();
                NativeBulkSetState prepared = early.NativeState ??= NativeBulkSetState.Create(early.InitialRoot.ValueHash256, AddressHash);
                NativePatriciaBulkSet.Update(early.NativeSource, prepared, _scope.NodeRecordBuffer.Native, early.NativeSource.WarmedNodes, entries.AsSpan());
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
            // Keep the disposed context as a tombstone: an accepted hint may still arrive after a failed pass.
            if (Interlocked.CompareExchange(ref _earlyState, EarlyIdle, EarlyApplying) != EarlyApplying) early.Dispose();
            OnEarlyPassCompleted?.Invoke();
        }
    }

    internal bool EarlyWritesDrained => _earlyTrie?.Writes.IsEmpty != false
        && Volatile.Read(ref _earlyState) != EarlyApplying && Volatile.Read(ref _earlyQueued) == 0;

    internal Action? OnEarlyHintAccepted;
    internal Action? OnEarlyPassDrained;
    internal Action? OnEarlyPassCompleted;

    internal NativeBulkSetState? EarlyNativeState => Volatile.Read(ref _earlyTrie)?.NativeState;

    private EarlyTrie? TakeEarlyTrie()
    {
        int previous = ClaimEarlyTrie();
        if (previous == EarlyApplying) _scope.CountEarlyAbandoned();
        return previous == EarlyIdle ? Interlocked.Exchange(ref _earlyTrie, null) : null;
    }

    private void AbandonEarlyTrie()
    {
        if (ClaimEarlyTrie() == EarlyIdle) Interlocked.Exchange(ref _earlyTrie, null)?.Dispose();
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

                _bundle.WarmUpNative(NativeStore, _scope.NodeRecordBuffer, AddressHash, _storageRoot, key, forDelete: isDelete);
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
        _nativeRoot = Keccak.EmptyTreeHash;
        if (!_scope.Trieless) NativeStore.Clear();
    }

    public void CommitTree() => _nativeStore?.Publish(_nativeRoot);

    /// <summary>Whether the storage trie holds nodes written since its last commit.</summary>
    internal bool HasUncommittedNodes => _nativeStore is { HasPending: true };

    internal void DiscardEarlyWrites() => AbandonEarlyTrie();

    /// <summary>Abandons private early updates once the scope no longer uses this tree.</summary>
    internal void ReleaseTrie()
    {
        AbandonEarlyTrie();
        _nativeStore?.Dispose();
    }

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);
        using EarlyTrie? early = TakeEarlyTrie();
        NativeBulkSetState? prepared = early?.NativeState;
        Dictionary<UInt256, UInt256>? applied = prepared is null ? null : early!.Applied;
        if (early is not null) early.NativeState = null;
        return new NativeStorageWriteBatch(this, estimatedEntries, onRootUpdated, prepared, applied);
    }

    private sealed class NativeStorageWriteBatch(
        FlatStorageTree storageTree,
        int estimatedEntries,
        Action<Address, Hash256> onRootUpdated,
        NativeBulkSetState? prepared,
        Dictionary<UInt256, UInt256>? earlyApplied) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private readonly ArrayPoolList<PatriciaTree.BulkSetEntry> _entries = new(estimatedEntries);
        private NativeBulkSetState? _prepared = prepared;
        private Dictionary<UInt256, UInt256>? _earlyApplied = earlyApplied;
        private bool _hasSelfDestruct;
        private bool _hasWrites;
        private int _reused;

        public void Set(in UInt256 index, in UInt256 value)
        {
            _hasWrites = true;
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
            if (_hasWrites) throw new InvalidOperationException("Must call clear first in a storage write batch");
            _prepared?.Dispose();
            _prepared = null;
            _earlyApplied = null;
            _hasSelfDestruct = true;
            storageTree.ClearStorage();
        }

        public void Dispose()
        {
            using NativeBulkSetState? prepared = _prepared;
            _prepared = null;
            using ArrayPoolList<PatriciaTree.BulkSetEntry> entries = _entries;
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
            if (prepared is not null) storageTree._nativeRoot = storageTree.NativeStore.Finish(prepared, entries.AsSpan());
            else if (entries.Count > 0) storageTree._nativeRoot = storageTree.NativeStore.BulkSet(storageTree._nativeRoot, entries.AsSpan());
            if (!storageTree._config.DeferStorageTrieCommit) storageTree.CommitTree();
            if (prepared is not null || entries.Count > 0 || _hasSelfDestruct) onRootUpdated(storageTree._address, storageTree._nativeRoot);
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
