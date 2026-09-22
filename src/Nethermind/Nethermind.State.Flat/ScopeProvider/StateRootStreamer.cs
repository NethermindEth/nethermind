// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Applies a block's committed account and storage changes to a flat scope's tries while the block executes, and
/// hashes them as it goes, so the roots at the end of the block only cover what changed last.
/// </summary>
/// <remarks>
/// <para>
/// Changes arrive in commit order through one feed and are drained on <see cref="StateRootStreamThreads"/>. Until
/// <see cref="Finish"/> only the drain touches the tries; <see cref="Finish"/> waits for it and does the rest on the
/// calling thread.
/// </para>
/// <para>
/// The tries end up holding what the end-of-block write batch writes, so the batch still runs and finds its values
/// already in place. A storage trie is only ever cleared where the batch clears it, and an account leaf carries the
/// root of its storage trie as the batch computes it. A failure rolls every trie back to the start of the block, which
/// leaves the batch its full work.
/// </para>
/// </remarks>
public sealed class StateRootStreamer : IDisposable
{
    private static readonly long HashIntervalTicks = Stopwatch.Frequency / 1000;

    private readonly StateTree _stateTree;
    private readonly StateRootStreamThreads _threads;
    private readonly ILogger _logger;

    private readonly Lock _feedLock = new();
    private ArrayPoolList<Change> _feed = new(64);
    private ArrayPoolList<Change>? _spareFeed = new(64);
    private volatile bool _draining;
    private volatile bool _closed;
    private volatile bool _faulted;

    // Owned by whoever holds the tries: a drain while one runs, the caller of Finish once the feed is closed.
    private readonly Dictionary<AddressAsKey, TouchedAccount> _touched = [];
    private readonly List<AddressAsKey> _dirtyStorage = [];
    private readonly List<AddressAsKey> _dirtyLeaves = [];
    private Hash256 _blockStartRoot;
    private long _lastHashTimestamp;
    private Hash256? _streamedRoot;

    internal StateRootStreamer(StateTree stateTree, Hash256 blockStartRoot, StateRootStreamThreads threads, ILogManager logManager)
    {
        _stateTree = stateTree;
        _blockStartRoot = blockStartRoot;
        _threads = threads;
        _logger = logManager.GetClassLogger<StateRootStreamer>();
    }

    internal bool AddSlot(FlatStorageTree storageTree, in UInt256 index, in UInt256 value) =>
        Add(new Change(ChangeKind.Slot, storageTree.Address, storageTree, null, index, value));

    internal bool AddClear(FlatStorageTree storageTree) =>
        Add(new Change(ChangeKind.Clear, storageTree.Address, storageTree, null, default, default));

    internal bool AddAccount(Address address, Account? account) =>
        Add(new Change(ChangeKind.Account, address, null, account, default, default));

    private bool Add(in Change change)
    {
        bool schedule;
        lock (_feedLock)
        {
            if (_closed || _faulted) return false;

            _feed.Add(change);
            schedule = !_draining;
            _draining = true;
        }

        // An unscheduled feed is applied by Finish instead.
        if (schedule && !_threads.TrySchedule(this))
        {
            lock (_feedLock) _draining = false;
        }

        return true;
    }

    // Runs on a StateRootStreamThreads thread. Never throws: a failure marks the streamer faulted, which Finish resolves
    // by rolling the tries back.
    internal void Drain()
    {
        try
        {
            while (true)
            {
                ArrayPoolList<Change>? changes = TakeFeed();
                if (changes is not null)
                {
                    Apply(changes);
                    RecycleFeed(changes);
                    continue;
                }

                // A quiet moment, and at most one round per interval: a node hashed now and changed again by a later
                // transaction is hashed twice.
                if (!_closed && HasUnhashedChanges && Stopwatch.GetTimestamp() - _lastHashTimestamp >= HashIntervalTicks)
                {
                    HashRound(parallel: false);
                    continue;
                }

                if (TryStopDraining()) return;
            }
        }
        catch (Exception exception)
        {
            _faulted = true;
            if (_logger.IsDebug) _logger.Debug($"State root streaming stopped, the block's roots are computed at its end: {exception}");
            lock (_feedLock)
            {
                // The batch that failed is dropped with the block's streamed work.
                _spareFeed ??= new ArrayPoolList<Change>(64);
                _draining = false;
            }
        }
    }

    /// <summary>
    /// Closes the feed, waits for the drain and applies and hashes whatever is left, leaving the tries for the
    /// end-of-block write batch. Does nothing once the feed is closed.
    /// </summary>
    public void Finish()
    {
        lock (_feedLock)
        {
            if (_closed) return;
            _closed = true;
        }

        WaitForDrain();

        if (_faulted)
        {
            RollBack();
            return;
        }

        try
        {
            Apply(_feed);
            _feed.Clear();
            HashRound(parallel: true);
            _streamedRoot = _stateTree.RootRef?.Keccak ?? Keccak.EmptyTreeHash;
        }
        catch (Exception exception)
        {
            _faulted = true;
            if (_logger.IsDebug) _logger.Debug($"State root streaming failed at the end of the block, its roots are computed in full: {exception}");
            RollBack();
        }
    }

    /// <summary>
    /// The account trie root the stream reached when <see cref="Finish"/> ran, for the first write batch after it to
    /// check its own against; null once taken or when the stream fell back.
    /// </summary>
    internal Hash256? TakeStreamedRoot()
    {
        Hash256? root = _streamedRoot;
        _streamedRoot = null;
        return root;
    }

    /// <summary>Reopens the feed for the next block, whose tries start at <paramref name="blockStartRoot"/>.</summary>
    /// <remarks>Call after <see cref="Finish"/>, once the block is committed.</remarks>
    public void StartBlock(Hash256 blockStartRoot)
    {
        Debug.Assert(_closed && !_draining, "a block starts only once the previous one is finished");

        ClearBlock();
        _blockStartRoot = blockStartRoot;
        _streamedRoot = null;
        _faulted = false;
        lock (_feedLock)
        {
            _feed.Clear();
            _closed = false;
        }
    }

    public void Dispose()
    {
        lock (_feedLock) _closed = true;
        WaitForDrain();
        _feed.Dispose();
        _spareFeed?.Dispose();
    }

    private bool HasUnhashedChanges => _dirtyStorage.Count > 0 || _dirtyLeaves.Count > 0;

    private ArrayPoolList<Change>? TakeFeed()
    {
        lock (_feedLock)
        {
            if (_closed || _feed.Count == 0) return null;

            ArrayPoolList<Change> changes = _feed;
            _feed = _spareFeed!;
            _spareFeed = null;
            return changes;
        }
    }

    private void RecycleFeed(ArrayPoolList<Change> changes)
    {
        changes.Clear();
        lock (_feedLock) _spareFeed = changes;
    }

    private bool TryStopDraining()
    {
        lock (_feedLock)
        {
            if (!_closed && _feed.Count > 0) return false;

            _draining = false;
            return true;
        }
    }

    // A drain holds tries the caller is about to use; its remaining work is bounded by one hash round.
    private void WaitForDrain()
    {
        SpinWait spinWait = new();
        while (_draining)
        {
            spinWait.SpinOnce(sleep1Threshold: -1);
        }
    }

    private void Apply(ArrayPoolList<Change> changes)
    {
        foreach (ref readonly Change change in changes.AsSpan())
        {
            AddressAsKey address = change.Address;
            ref TouchedAccount touched = ref CollectionsMarshal.GetValueRefOrAddDefault(_touched, address, out _);
            switch (change.Kind)
            {
                case ChangeKind.Account:
                    touched.Account = change.Account;
                    touched.HasAccount = true;
                    MarkLeafDirty(ref touched, address);
                    break;
                case ChangeKind.Slot:
                    touched.Storage = change.Storage;
                    change.Storage!.StreamSet(change.Index, change.Value);
                    MarkStorageDirty(ref touched, address);
                    break;
                case ChangeKind.Clear:
                    touched.Storage = change.Storage;
                    change.Storage!.StreamClear();
                    MarkStorageDirty(ref touched, address);
                    break;
            }
        }
    }

    private void MarkStorageDirty(ref TouchedAccount touched, AddressAsKey address)
    {
        if (touched.StorageDirty) return;
        touched.StorageDirty = true;
        _dirtyStorage.Add(address);
    }

    private void MarkLeafDirty(ref TouchedAccount touched, AddressAsKey address)
    {
        if (touched.LeafDirty) return;
        touched.LeafDirty = true;
        _dirtyLeaves.Add(address);
    }

    // Storage tries first, then the account leaves that carry their roots, then the account trie.
    private void HashRound(bool parallel)
    {
        if (_dirtyStorage.Count > 0) HashStorage(parallel);

        foreach (AddressAsKey address in _dirtyLeaves)
        {
            ref TouchedAccount touched = ref CollectionsMarshal.GetValueRefOrNullRef(_touched, address);
            touched.LeafDirty = false;
            if (TryGetLeaf(address.Value, in touched, out Account? leaf)) _stateTree.Set(address.Value, leaf);
        }

        _dirtyLeaves.Clear();
        _stateTree.HashDirtyNodes(canBeParallel: parallel);
        _lastHashTimestamp = Stopwatch.GetTimestamp();
    }

    private void HashStorage(bool parallel)
    {
        int count = _dirtyStorage.Count;
        using ArrayPoolList<(FlatStorageTree Storage, Hash256? Root)> storages = new(count, count);
        for (int i = 0; i < count; i++)
        {
            storages[i] = (CollectionsMarshal.GetValueRefOrNullRef(_touched, _dirtyStorage[i]).Storage!, null);
        }

        if (parallel && count > 1)
        {
            ParallelUnbalancedWork.For(
                0,
                count,
                ParallelUnbalancedWork.DefaultOptions,
                storages,
                static (i, storages) =>
                {
                    ref (FlatStorageTree Storage, Hash256? Root) entry = ref storages.GetRef(i);
                    entry.Root = entry.Storage.StreamHash();
                    return storages;
                });
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                ref (FlatStorageTree Storage, Hash256? Root) entry = ref storages.GetRef(i);
                entry.Root = entry.Storage.StreamHash();
            }
        }

        for (int i = 0; i < count; i++)
        {
            AddressAsKey address = _dirtyStorage[i];
            ref TouchedAccount touched = ref CollectionsMarshal.GetValueRefOrNullRef(_touched, address);
            touched.StorageDirty = false;
            touched.StorageRoot = storages[i].Root;
            MarkLeafDirty(ref touched, address);
        }

        _dirtyStorage.Clear();
    }

    // The leaf the write batch will write: the block's last account write, or the account as it stands when only its
    // storage changed, carrying the root of its storage trie when the block touched it.
    private bool TryGetLeaf(Address address, in TouchedAccount touched, out Account? leaf)
    {
        if (touched.HasAccount)
        {
            Account? account = touched.Account;
            leaf = account is null || touched.StorageRoot is null ? account : account.WithChangedStorageRoot(touched.StorageRoot);
            return true;
        }

        Account? current = touched.StorageRoot is null ? null : _stateTree.Get(address);
        leaf = current?.WithChangedStorageRoot(touched.StorageRoot!);
        return current is not null;
    }

    private void RollBack()
    {
        Metrics.RecordStateRootStreamFallback();
        _streamedRoot = null;
        foreach (TouchedAccount touched in _touched.Values)
        {
            touched.Storage?.StreamRollBack();
        }

        _stateTree.RootHash = _blockStartRoot;
        ClearBlock();
    }

    private void ClearBlock()
    {
        _touched.Clear();
        _dirtyStorage.Clear();
        _dirtyLeaves.Clear();
        _lastHashTimestamp = 0;
    }

    private enum ChangeKind : byte
    {
        Account,
        Slot,
        Clear,
    }

    private readonly record struct Change(
        ChangeKind Kind,
        Address Address,
        FlatStorageTree? Storage,
        Account? Account,
        UInt256 Index,
        UInt256 Value);

    private struct TouchedAccount
    {
        public Account? Account;
        public bool HasAccount;
        public FlatStorageTree? Storage;
        public Hash256? StorageRoot;
        public bool StorageDirty;
        public bool LeafDirty;
    }
}
