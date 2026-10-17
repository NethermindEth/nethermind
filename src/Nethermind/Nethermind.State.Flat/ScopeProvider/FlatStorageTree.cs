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
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;

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

    // Created on the owner thread by the first write batch or verified read; null in a trie-less scope.
    private LoggedTrie? _trie;
    // The root a trie created from now on starts at.
    private Hash256 _rootBeforeTrie = storageRoot;
    // The root warm-ups walk from, resolved by the first one on a warmer thread.
    private StrongBox<TrieRoot>? _preBlockRoot;

    private const int EarlyIdle = 0;
    private const int EarlyApplying = 1;
    private const int EarlyClaimed = 2;
    private const int EarlyAbandoned = 3;

    private EarlyTrie? _earlyTrie;
    private int _earlyState;
    private int _earlyQueued;
    private readonly int _earlyGeneration = scope.EarlyApplyGeneration;

    private sealed class EarlyTrie : IDisposable
    {
        public readonly SnapshotBundle.BackgroundTrieSource Source;
        public readonly WrittenNodes Written = new();
        public readonly ConcurrentQueue<(UInt256 Slot, UInt256 Value)> Writes = new();
        public readonly TrieNodeLog Log;
        public readonly Dictionary<UInt256, UInt256> Applied = [];
        public TrieRoot Root;

        public EarlyTrie(SnapshotBundle bundle, Hash256 address, Hash256 root)
        {
            Log = bundle.TrieNodeLog!;
            Source = bundle.CreateBackgroundTrieSource(address);
            try
            {
                Root = LogBulkSet.ResolveRoot(Log, address, root.ValueHash256);
            }
            catch
            {
                Source.Dispose();
                Written.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Written.Dispose();
            Source.Dispose();
        }
    }

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

    private TrieRoot PreBlockRoot => (Volatile.Read(ref _preBlockRoot) ?? ResolvePreBlockRoot()).Value;

    private StrongBox<TrieRoot> ResolvePreBlockRoot()
    {
        StrongBox<TrieRoot> resolved = new(LogBulkSet.ResolveRoot(_bundle.TrieNodeLog!, AddressHash, _storageRoot.ValueHash256));
        Volatile.Write(ref _preBlockRoot, resolved);
        return resolved;
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
    public void HintSet(in UInt256 index) => WarmUpSlot(index, isDelete: false);

    /// <inheritdoc/>
    public void HintSet(in UInt256 index, in UInt256 value)
    {
        WarmUpSlot(index, isDelete: value.IsZero);
        if (!_scope.AppliesStorageWritesEarly || Volatile.Read(ref _earlyState) >= EarlyClaimed) return;

        OnEarlyHintAccepted?.Invoke();
        EarlyTrie early = _earlyTrie ??= new EarlyTrie(_bundle, AddressHash, _rootBeforeTrie);
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
                // Pool work would run at normal priority rather than this thread's idle priority.
                early.Root = LogBulkSet.BulkSet(early.Log, early.Source, AddressHash, early.Root, entries, early.Written, canParallelize: false);
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

    private Dictionary<UInt256, UInt256>? AdoptEarlyTrie(LoggedTrie trie)
    {
        int previous = ClaimEarlyTrie();
        if (previous == EarlyApplying)
        {
            _scope.CountEarlyAbandoned();
            return null;
        }

        if (previous != EarlyIdle) return null;
        using EarlyTrie? early = Interlocked.Exchange(ref _earlyTrie, null);
        if (early is not { Applied.Count: > 0 }) return null;
        trie.Adopt(early.Root, early.Written);
        return early.Applied;
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

    private void Set(in UInt256 slot, in UInt256 value) => _bundle.SetChangedSlot(_address, slot, value);

    internal void ClearStorage()
    {
        AbandonEarlyTrie();
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
    internal void ReleaseTrie()
    {
        AbandonEarlyTrie();
        _trie?.Dispose();
    }

    public IWorldStateScopeProvider.IStorageWriteBatch CreateWriteBatch(int estimatedEntries, Action<Address, Hash256> onRootUpdated)
    {
        if (_scope.Trieless) return new FlatOverlayStorageWriteBatch(this);
        LoggedTrie trie = Trie;
        return new LoggedStorageWriteBatch(this, trie, estimatedEntries, onRootUpdated, AdoptEarlyTrie(trie));
    }

    // Normal scope: maintain the storage trie (for the root) and mirror values into the flat overlay.
    private sealed class LoggedStorageWriteBatch(
        FlatStorageTree storageTree,
        LoggedTrie trie,
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
