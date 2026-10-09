// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides the read/write surface for a processing branch backed by one canonical EIP-8297 tree.</summary>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope
{
    private const int AccountGroupDepth = PbtNodeGroupLayout.AccountTopDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
    private const int StorageGroupDepth = PbtNodeGroupLayout.StemTopDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
    private const long AverageNodeGroupBytes = 1024;
    private const int SlotChunkLength = 64;

    private static long _nextScopeId;
    private readonly long _scopeId = Interlocked.Increment(ref _nextScopeId);
    private readonly ILogger _logger;
    private readonly ConcurrencyController _foldQuota;
    private readonly FoldFanOut _foldFanOut;
    private readonly IRefCountingMemoryProvider _nodeGroupMemory;
    private readonly IPbtCommitTarget _commitTarget;
    private readonly IPbtChildHeaderSource _childHeaders;
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];
    private readonly BackgroundTask _balPrefetch;

    private StateId _currentStateId;
    private Hash256 _rootHash;
    private ValueHash256 _treeRoot;
    private Hash256? _authoritativeRoot;
    private BlockHeader? _currentHeader;
    private BlockHeader? _childHeader;
    private bool _rootDirty;
    private bool _isDisposed;

    public PbtWorldStateScope(
        in StateId currentStateId,
        BlockHeader? currentHeader,
        PbtSnapshotBundle bundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IPbtCommitTarget commitTarget,
        IPbtChildHeaderSource childHeaders,
        IRefCountingMemoryProvider nodeGroupMemory,
        IPbtConfig config,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<PbtWorldStateScope>();
        _balPrefetch = new BackgroundTask(_logger, "HintBal prefetch");
        _foldQuota = new ConcurrencyController(config.FoldConcurrency > 0 ? config.FoldConcurrency : Environment.ProcessorCount);
        _foldFanOut = new(config.FoldMinOperationsPerWorker, config.FoldLargeSubtreeBytes, config.FoldLargeSubtreeMinOperationsPerWorker);
        _nodeGroupMemory = nodeGroupMemory;
        _currentStateId = currentStateId;
        _currentHeader = currentHeader;
        Bundle = bundle;
        _commitTarget = commitTarget;
        _childHeaders = childHeaders;
        _treeRoot = bundle.TreeRoot;
        _rootHash = currentStateId.StateRoot.ToHash256();
        CodeDb = new PbtCodeDb(codeDb, Bundle);
        if (_logger.IsDebug) LogLifecycle("opened");
    }

    private void LogLifecycle(string stage) =>
        _logger.Debug($"PBT scope {_scopeId} {stage}: state={_currentStateId}, pendingMutations={Bundle.PendingMutationCount}, managedBytes={GC.GetTotalMemory(false)}");

    internal PbtSnapshotBundle Bundle { get; }
    public Hash256 RootHash => _rootHash;

    // PBT has no per-account storage root.
    public bool StorageRootsAreAuthoritative => false;
    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    internal void UseAuthoritativeRoot(Hash256 root)
    {
        _authoritativeRoot = root;
        _rootHash = root;
    }

    public Account? Get(Address address) => Bundle.GetAndPromoteAccount(address);

    public void HintGet(Address address, Account? account) => Bundle.HintAccount(address, account);

    /// <summary>
    /// Starts buffering the accounts and slot runs <paramref name="bal"/> writes, so <see cref="ApplyBal"/> finds them in the write buffer,
    /// and then prefetching the node groups the fold of those writes walks into, both from the list's keys derived once and sorted.
    /// </summary>
    /// <remarks>
    /// The buffered values are the ones visible when read, and a buffered value never replaces a write, so the prefetch
    /// may run alongside writes. Clearing storage could still race the prefetch of a run of the cleared account, so
    /// clearing, like committing and disposing, stops the buffering first.
    /// With a <paramref name="sink"/>, every account and slot the list names, read-only ones included, is also read and forwarded to it.
    /// The returned task does not cover the node group prefetch, which <see cref="UpdateRootHash"/> stops.
    /// </remarks>
    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
    {
        _balPrefetch.Stop();
        if (bal.AccountChanges.Count == 0) return Task.CompletedTask;
        BalKeys? keys = null;
        return _balPrefetch.Start(
            reads =>
            {
                keys = BalKeys.Create(bal, sink is not null);
                try
                {
                    PrefetchBal(Bundle, keys, sink, reads);
                }
                catch
                {
                    keys.Dispose();
                    throw;
                }
            },
            cancellation =>
            {
                using (keys) PrefetchNodeGroups(Bundle, keys!, cancellation);
            });
    }

    /// <summary>Buffers the accounts and slot runs the keys of a block access list write, forwarding every account and slot it names to <paramref name="sink"/>.</summary>
    /// <remarks>
    /// Accounts are read first, so their slots know whether the account is missing: <see cref="ApplyBal"/> writes no slot of a
    /// missing account that the block leaves missing. The slots of one run are adjacent in key order, so the run is buffered once.
    /// </remarks>
    internal static void PrefetchBal(PbtSnapshotBundle bundle, BalKeys keys, IWorldStateScopeProvider.IAsyncBalReaderSink? sink, CancellationToken reads)
    {
        ParallelUnbalancedWork.For(0, keys.Accounts.Count, (bundle, keys, sink, reads), static (position, state) =>
        {
            if (state.reads.IsCancellationRequested) return state;
            int index = (int)state.keys.Accounts[position].Value.ToUInt256().u0;
            ReadOnlyAccountChanges accountChanges = state.keys.Bal.AccountChanges.AsSpan()[index];
            Account? account = state.bundle.ReadAccount(Eip8297KeyDerivation.AddressHashOf(state.keys.Accounts[position].Key), promote: accountChanges.HasStateChanges);
            if (state.sink?.StillNeeded(accountChanges.Address, out _) == true) state.sink.OnAccountRead(accountChanges.Address, account);
            state.keys.Missing[index] = account is null;
            return state;
        });
        BufferSlots(bundle, keys, keys.HeaderWrites, sink, reads);
        BufferSlots(bundle, keys, keys.StorageWrites, sink, reads);
        if (sink is null) return;
        ReadSlotsToSink(bundle, keys, keys.HeaderReads, sink, reads);
        ReadSlotsToSink(bundle, keys, keys.StorageReads, sink, reads);
    }

    private static void BufferSlots<TKey>(PbtSnapshotBundle bundle, BalKeys keys, ArrayPoolList<PbtWriteOperation<TKey>> slots,
        IWorldStateScopeProvider.IAsyncBalReaderSink? sink, CancellationToken reads) where TKey : struct, IPbtKey<TKey> =>
        ParallelUnbalancedWork.For(0, (slots.Count + SlotChunkLength - 1) / SlotChunkLength, (bundle, keys, slots, sink, reads), static (chunk, state) =>
        {
            TKey? bufferedRun = null;
            for (int position = chunk * SlotChunkLength; position < Math.Min(state.slots.Count, (chunk + 1) * SlotChunkLength); position++)
            {
                if (state.reads.IsCancellationRequested) return state;
                TKey key = state.slots[position].Key;
                ValueHash256 addressHash = Eip8297KeyDerivation.AddressHashOf(key);
                if (state.keys.WritesNothing(addressHash, out int index)) continue;
                TKey run = SlotRun.RunKey(key);
                if (!state.keys.Missing[index] && ReadSlotToSink(state.bundle, state.sink, state.keys.Bal.AccountChanges.AsSpan()[index].Address, addressHash, key, state.slots[position].Value))
                {
                    bufferedRun = run;
                    continue;
                }
                if (bufferedRun is { } previous && previous.Equals(run)) continue;
                state.bundle.GetSlot(key, addressHash);
                bufferedRun = run;
            }
            return state;
        });

    private static void ReadSlotsToSink<TKey>(PbtSnapshotBundle bundle, BalKeys keys, ArrayPoolList<PbtWriteOperation<TKey>> slots,
        IWorldStateScopeProvider.IAsyncBalReaderSink sink, CancellationToken reads) where TKey : struct, IPbtKey<TKey> =>
        ParallelUnbalancedWork.For(0, slots.Count, (bundle, keys, slots, sink, reads), static (position, state) =>
        {
            if (state.reads.IsCancellationRequested) return state;
            TKey key = state.slots[position].Key;
            ValueHash256 addressHash = Eip8297KeyDerivation.AddressHashOf(key);
            int index = state.keys.AccountIndex(addressHash);
            if (!state.keys.Missing[index])
                ReadSlotToSink(state.bundle, state.sink, state.keys.Bal.AccountChanges.AsSpan()[index].Address, addressHash, key, state.slots[position].Value);
            return state;
        });

    /// <summary>Reads the node groups the fold of the writes in <paramref name="keys"/> walks into, so the bundle keeps the persisted ones for the fold.</summary>
    /// <remarks>
    /// Reads the first group below the top node groups of each account zone written, and per account writing its storage
    /// zone, the first storage group and then the groups below each written slot, as deep as the slot's estimated remaining
    /// group levels: a boundary slot of the first storage group holds about <c>descendantBytes / 1 KiB</c> groups, so its
    /// remaining depth is about <c>log16</c> of that many group levels. Code leaves are left out: they are content
    /// addressed, so a deployment rarely finds their groups stored.
    /// </remarks>
    internal static void PrefetchNodeGroups(PbtSnapshotBundle bundle, BalKeys keys, CancellationToken cancellation)
    {
        HashSet<PbtStorageNodePath> accountGroups = [];
        foreach (PbtWriteOperation<PbtPath> account in keys.Accounts)
        {
            PbtPath key = account.Key;
            if (keys.Bal.AccountChanges.AsSpan()[(int)account.Value.ToUInt256().u0].HasBalanceNonceOrCodeChanges) accountGroups.Add(GroupOf(key.Bytes, AccountGroupDepth));
        }
        foreach (PbtWriteOperation<PbtPath> slot in keys.HeaderWrites)
        {
            PbtPath key = slot.Key;
            if (!keys.WritesNothing(Eip8297KeyDerivation.AddressHashOf(key), out _)) accountGroups.Add(GroupOf(key.Bytes, AccountGroupDepth));
        }
        PbtStorageNodePath[] accountGroupPaths = [.. accountGroups];
        using ArrayPoolList<Range> storageAccounts = keys.StorageAccounts();
        ParallelUnbalancedWork.For(0, accountGroupPaths.Length + storageAccounts.Count, (bundle, keys, accountGroupPaths, storageAccounts, cancellation), static (item, state) =>
        {
            if (state.cancellation.IsCancellationRequested) return state;
            if (item < state.accountGroupPaths.Length) ((IDisposable?)ReadNodeGroup(state.bundle, state.accountGroupPaths[item]))?.Dispose();
            else PrefetchStorageGroups(state.bundle, state.keys.StorageWrites.AsSpan()[state.storageAccounts[item - state.accountGroupPaths.Length]], state.cancellation);
            return state;
        });
    }

    /// <summary>Reads the first storage group of one account and the groups below its sorted written slots.</summary>
    /// <remarks>A group shared with the previous slot's path is read for that slot already, as both lie under one boundary slot and so share their depth.</remarks>
    private static void PrefetchStorageGroups(PbtSnapshotBundle bundle, ReadOnlySpan<PbtWriteOperation<PbtStoragePath>> slots, CancellationToken cancellation)
    {
        PbtStoragePath previous = slots[0].Key;
        Span<long> descendantBytes = stackalloc long[PbtFourLevelGroupGeometry.BoundarySlots];
        using (RefCountingMemory? storageGroup = ReadNodeGroup(bundle, GroupOf(previous.Bytes, StorageGroupDepth)))
            if (storageGroup is not null) PbtNodeGroupCodec.ReadDescendantBytes(storageGroup.GetSpan(), descendantBytes);

        for (int position = 0; position < slots.Length; position++)
        {
            PbtStoragePath key = slots[position].Key;
            int sharedBits = position == 0 ? StorageGroupDepth : previous.FirstDifferingBit(key, 0);
            previous = key;
            long subtreeBytes = descendantBytes[TrieUpdater.BoundarySlot(key.Bytes, StorageGroupDepth)];
            int levels = subtreeBytes <= AverageNodeGroupBytes ? 0 : (int)Math.Ceiling(Math.Log((double)subtreeBytes / AverageNodeGroupBytes, 16));
            for (int level = 1; level <= levels; level++)
            {
                int depth = StorageGroupDepth + level * PbtFourLevelGroupGeometry.LevelsPerGroup;
                if (depth <= sharedBits) continue;
                if (cancellation.IsCancellationRequested) return;
                ((IDisposable?)ReadNodeGroup(bundle, GroupOf(key.Bytes, depth)))?.Dispose();
            }
        }
    }

    /// <summary>The path of the group at <paramref name="depth"/> above the leaf at <paramref name="key"/>.</summary>
    private static PbtStorageNodePath GroupOf(ReadOnlySpan<byte> key, int depth)
    {
        Span<byte> path = stackalloc byte[(depth + 7) / 8];
        key[..path.Length].CopyTo(path);
        if (depth % 8 != 0) path[^1] &= 0xF0;
        return new PbtStorageNodePath(path, depth);
    }

    /// <summary>Reads a group from the snapshots, or else from persistence.</summary>
    /// <returns>A caller-owned group lease, or null when the group is absent.</returns>
    private static RefCountingMemory? ReadNodeGroup(PbtSnapshotBundle bundle, PbtStorageNodePath path) =>
        bundle.TryGetSnapshotNodeGroup(path, out RefCountingMemory? snapshot) ? snapshot : bundle.GetPersistedNodeGroup(path);

    /// <summary>Reads the slot and forwards it to <paramref name="sink"/> when the sink still needs it.</summary>
    /// <returns>Whether the slot was read.</returns>
    private static bool ReadSlotToSink<TKey>(PbtSnapshotBundle bundle, IWorldStateScopeProvider.IAsyncBalReaderSink? sink, Address address, in ValueHash256 addressHash,
        in TKey key, in ValueHash256 slot) where TKey : struct, IPbtKey<TKey>
    {
        StorageCell cell = new(address, slot.ToUInt256());
        if (sink?.StillNeeded(in cell) != true) return false;
        UInt256 value = bundle.GetSlot(key, addressHash);
        sink.OnStorageRead(in cell, value);
        return true;
    }

    public void ApplyBal(ReadOnlyBlockAccessList bal) => ScopeBalApplier.ApplyConcurrently(this, bal);

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
    {
        lock (_storages)
        {
            ref PbtStorageTree? tree = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
            if (!exists) tree = new PbtStorageTree(this, address);
            return tree!;
        }
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatch(this);

    public void UpdateRootHash()
    {
        try
        {
            if (!_rootDirty) return;
            if (_logger.IsDebug) LogLifecycle("root calculation begin");
            long start = Stopwatch.GetTimestamp();
            PbtPartitionBatches changes = Bundle.PrepareLeafChanges();
            // Counting walks every shard of every partition, and the fold below drains them, so read it only when logged.
            int mutationCount = _logger.IsDebug ? Bundle.PendingMutationCount : 0;
            try
            {
                Metrics.PbtPrepareLeafChangesTime.Observe(Stopwatch.GetTimestamp() - start);
                long updaterStart = Stopwatch.GetTimestamp();
                using (PbtSnapshotStore store = new(Bundle))
                    _treeRoot = TrieUpdater.UpdateRoot(store, _treeRoot, changes, _foldQuota, _foldFanOut, Metrics.PbtPartitionFoldTime, memoryProvider: _nodeGroupMemory);
                Metrics.PbtTrieUpdaterTime.Observe(Stopwatch.GetTimestamp() - updaterStart);
                Bundle.CompleteLeafChanges();
            }
            finally
            {
                changes.Dispose();
            }
            Metrics.PbtRootHashTime.Observe(Stopwatch.GetTimestamp() - start);
            _childHeader ??= _currentHeader is null ? null : _childHeaders.TryFindChild(_currentHeader);
            _rootHash = _authoritativeRoot ?? _childHeader?.StateRoot ?? _treeRoot.ToHash256();
            _rootDirty = false;
            if (_logger.IsDebug) LogLifecycle($"root calculated treeRoot={_treeRoot}, mutations={mutationCount}, elapsed={Stopwatch.GetElapsedTime(start)}");
        }
        finally
        {
            _balPrefetch.Stop();
        }
    }

    public void Commit(ulong blockNumber)
    {
        if (_logger.IsDebug) LogLifecycle($"commit begin block={blockNumber}");
        long commitStart = Stopwatch.GetTimestamp();
        try
        {
            _balPrefetch.StopFirstPhase();
            UpdateRootHash();
            StateId newStateId = new(blockNumber, _rootHash);
            if (newStateId != _currentStateId)
            {
                long addSnapshotStart = Stopwatch.GetTimestamp();
                PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _treeRoot, out PbtTransientResource transientResource);
                _commitTarget.AddSnapshot(snapshot, transientResource);
                Metrics.PbtAddSnapshotTime.Observe(Stopwatch.GetTimestamp() - addSnapshotStart);
                _currentStateId = newStateId;
            }
            _currentHeader = _childHeader;
            _childHeader = null;
            lock (_storages) _storages.Clear();
            if (_logger.IsDebug) LogLifecycle($"commit completed block={blockNumber}");
        }
        finally
        {
            Metrics.PbtCommitTime.Observe(Stopwatch.GetTimestamp() - commitStart);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_logger.IsDebug) LogLifecycle("close begin");
        try
        {
            _balPrefetch.Stop();
            Bundle.Dispose();
        }
        finally
        {
            if (_logger.IsDebug) _logger.Debug($"PBT scope {_scopeId} closed: state={_currentStateId}, managedBytes={GC.GetTotalMemory(false)}");
        }
    }

    private sealed class WriteBatch(PbtWorldStateScope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated { add { } remove { } }

        public void Set(Address key, Account? account)
        {
            // Removing an account clears its storage.
            if (account is null) scope._balPrefetch.StopFirstPhase();
            scope.Bundle.SetAccount(key, account);
            scope._rootDirty = true;
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(scope, key, estimatedEntries);

        public void Dispose() => Metrics.PbtWriteBatchTime.Observe(Stopwatch.GetTimestamp() - _start);
    }

    private sealed class StorageWriteBatch(PbtWorldStateScope scope, Address address, int estimatedEntries) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        // One batch serves one contract on one thread, so the address hash is derived once for the whole run of slots.
        private readonly ValueHash256 _addressHash = PbtStateKey.AddressKeyHash(address);
        // Applied together, so each slot run the writes touch is rewritten once for all of its adjacent writes.
        private readonly ArrayPoolList<SlotWrite> _writes = new(estimatedEntries);

        public void Set(in UInt256 index, in UInt256 value)
        {
            _writes.Add(new SlotWrite(index, value));
            scope._rootDirty = true;
        }

        public void Clear()
        {
            ApplyWrites();
            scope._balPrefetch.StopFirstPhase();
            scope.Bundle.SelfDestruct(_addressHash);
            scope._rootDirty = true;
        }

        public void Dispose()
        {
            ApplyWrites();
            _writes.Dispose();
        }

        private void ApplyWrites()
        {
            scope.Bundle.SetSlots(address, _addressHash, _writes.AsSpan());
            _writes.Clear();
        }
    }

    /// <summary>The keys a block access list names, as EIP-8297 paths sorted per list, so the slots of one run, stem or group are adjacent.</summary>
    internal sealed class BalKeys : IDisposable
    {
        private readonly Dictionary<ValueHash256, int> _accountIndexes = [];

        private BalKeys(ReadOnlyBlockAccessList bal)
        {
            Bal = bal;
            Missing = new bool[bal.AccountChanges.Count];
            Accounts = new(bal.AccountChanges.Count);
        }

        public ReadOnlyBlockAccessList Bal { get; }

        /// <summary>The basic-data key of each account to read, with its index in <see cref="Bal"/> as the value.</summary>
        public ArrayPoolList<PbtWriteOperation<PbtPath>> Accounts { get; }

        /// <summary>The written header slots, with the slot as the value.</summary>
        public ArrayPoolList<PbtWriteOperation<PbtPath>> HeaderWrites { get; } = new(0);

        /// <summary>The written storage-zone slots, with the slot as the value.</summary>
        public ArrayPoolList<PbtWriteOperation<PbtStoragePath>> StorageWrites { get; } = new(0);

        /// <summary>The header slots only read, with the slot as the value.</summary>
        public ArrayPoolList<PbtWriteOperation<PbtPath>> HeaderReads { get; } = new(0);

        /// <summary>The storage-zone slots only read, with the slot as the value.</summary>
        public ArrayPoolList<PbtWriteOperation<PbtStoragePath>> StorageReads { get; } = new(0);

        /// <summary>Per account in <see cref="Bal"/>, whether reading it found it missing.</summary>
        public bool[] Missing { get; }

        /// <param name="withReads">Whether to also collect the accounts and slots the list only reads.</param>
        public static BalKeys Create(ReadOnlyBlockAccessList bal, bool withReads)
        {
            BalKeys keys = new(bal);
            ReadOnlySpan<ReadOnlyAccountChanges> accountChanges = bal.AccountChanges.AsSpan();
            for (int index = 0; index < accountChanges.Length; index++)
            {
                ReadOnlyAccountChanges changes = accountChanges[index];
                if (!changes.HasStateChanges && !withReads) continue;
                ValueHash256 addressHash = PbtStateKey.AddressKeyHash(changes.Address);
                keys._accountIndexes[addressHash] = index;
                keys.Accounts.Add(new(Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.BasicDataLeafKey), ((UInt256)(ulong)index).ToValueHash()));
                StemKeys writes = new(changes.Address, addressHash);
                foreach (ReadOnlySlotChanges slotChanges in changes.StorageChanges)
                    if (slotChanges.Changes.Length != 0) writes.Add(slotChanges.Key, keys.HeaderWrites, keys.StorageWrites);
                if (!withReads) continue;
                StemKeys readKeys = new(changes.Address, addressHash);
                foreach (UInt256 slot in changes.StorageReads) readKeys.Add(slot, keys.HeaderReads, keys.StorageReads);
            }
            PbtOperationSort.Sort(keys.Accounts.AsSpan());
            PbtOperationSort.Sort(keys.HeaderWrites.AsSpan());
            PbtOperationSort.Sort(keys.StorageWrites.AsSpan());
            PbtOperationSort.Sort(keys.HeaderReads.AsSpan());
            PbtOperationSort.Sort(keys.StorageReads.AsSpan());
            return keys;
        }

        public int AccountIndex(in ValueHash256 addressHash) => _accountIndexes[addressHash];

        /// <summary>Whether <see cref="ApplyBal"/> writes nothing to the account: it is missing and the block leaves it missing.</summary>
        public bool WritesNothing(in ValueHash256 addressHash, out int index)
        {
            index = AccountIndex(addressHash);
            return Missing[index] && !Bal.AccountChanges.AsSpan()[index].HasBalanceNonceOrCodeChanges;
        }

        /// <summary>The ranges of <see cref="StorageWrites"/> that each hold one account's slots, leaving out accounts <see cref="WritesNothing"/> holds for.</summary>
        public ArrayPoolList<Range> StorageAccounts()
        {
            ArrayPoolList<Range> ranges = new(0);
            for (int start = 0, end; start < StorageWrites.Count; start = end)
            {
                ValueHash256 addressHash = Eip8297KeyDerivation.AddressHashOf(StorageWrites[start].Key);
                for (end = start + 1; end < StorageWrites.Count && Eip8297KeyDerivation.AddressHashOf(StorageWrites[end].Key) == addressHash; end++) { }
                if (!WritesNothing(addressHash, out _)) ranges.Add(start..end);
            }
            return ranges;
        }

        public void Dispose()
        {
            Accounts.Dispose();
            HeaderWrites.Dispose();
            StorageWrites.Dispose();
            HeaderReads.Dispose();
            StorageReads.Dispose();
        }

        /// <summary>Derives the keys of one account's slots, given in ascending order, deriving each stem's hash once.</summary>
        private struct StemKeys(Address address, in ValueHash256 addressHash)
        {
            private readonly ValueHash256 _addressHash = addressHash;
            private UInt256? _stemSlot;
            private PbtStoragePath _stemKey;

            public void Add(in UInt256 slot, ArrayPoolList<PbtWriteOperation<PbtPath>> headerKeys, ArrayPoolList<PbtWriteOperation<PbtStoragePath>> storageKeys)
            {
                if (Eip8297KeyDerivation.IsHeaderSlot(slot))
                {
                    headerKeys.Add(new(Eip8297KeyDerivation.HeaderStorageKey(_addressHash, slot), slot.ToValueHash()));
                    return;
                }
                if (_stemSlot is not { } stemSlot || !Eip8297KeyDerivation.InSameStem(stemSlot, slot))
                {
                    _stemKey = PbtStateKey.Storage(address, _addressHash, slot);
                    _stemSlot = slot;
                }
                storageKeys.Add(new(Eip8297KeyDerivation.StorageKeyInStem(_stemKey, slot), slot.ToValueHash()));
            }
        }
    }

    /// <summary>A two-phase background task that can be cancelled and waited for from any thread, as a whole or up to the end of its first phase.</summary>
    private sealed class BackgroundTask(ILogger logger, string name)
    {
        private readonly Lock _lock = new();
        private Task? _task;
        private Task? _firstPhase;
        private CancellationTokenSource? _cancellation;
        private CancellationTokenSource? _firstPhaseCancellation;

        /// <summary>Stops the running task, then runs <paramref name="firstPhase"/> and then <paramref name="secondPhase"/> in the background.</summary>
        /// <returns>A task that completes with the first phase.</returns>
        public Task Start(Action<CancellationToken> firstPhase, Action<CancellationToken> secondPhase)
        {
            Stop();
            lock (_lock)
            {
                CancellationTokenSource cancellation = new();
                CancellationTokenSource firstPhaseCancellation = new();
                TaskCompletionSource firstPhaseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _cancellation = cancellation;
                _firstPhaseCancellation = firstPhaseCancellation;
                _firstPhase = firstPhaseCompletion.Task;
                // Not cancellable before it starts, so the first phase always completes.
                _task = Task.Run(() =>
                {
                    try
                    {
                        firstPhase(firstPhaseCancellation.Token);
                        firstPhaseCompletion.SetResult();
                    }
                    catch (Exception ex)
                    {
                        firstPhaseCompletion.SetException(ex);
                        throw;
                    }
                    secondPhase(cancellation.Token);
                });
                return _firstPhase;
            }
        }

        /// <summary>Cancels the first phase and waits for it to finish, leaving the second phase running.</summary>
        public void StopFirstPhase()
        {
            if (Volatile.Read(ref _task) is null) return;
            lock (_lock)
            {
                _firstPhaseCancellation?.Cancel();
                try
                {
                    _firstPhase?.GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Stop logs the fault.
                }
            }
        }

        /// <summary>Cancels the running task and waits for it to finish.</summary>
        public void Stop()
        {
            if (Volatile.Read(ref _task) is null) return;
            lock (_lock)
            {
                _firstPhaseCancellation?.Cancel();
                _cancellation?.Cancel();
                try
                {
                    _task?.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (logger.IsError) logger.Error($"PBT {name} faulted", ex);
                }
                _firstPhaseCancellation?.Dispose();
                _firstPhaseCancellation = null;
                _cancellation?.Dispose();
                _cancellation = null;
                _firstPhase = null;
                Volatile.Write(ref _task, null);
            }
        }
    }
}
