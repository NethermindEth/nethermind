// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides the read/write surface for a processing branch backed by one canonical EIP-8297 tree.</summary>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope
{
    private static long _nextScopeId;
    private readonly long _scopeId = Interlocked.Increment(ref _nextScopeId);
    private readonly ILogger _logger;
    private readonly PbtResourcePool.Usage _usage;
    private readonly ConcurrencyController _foldQuota;
    private readonly FoldFanOut _foldFanOut;
    private readonly long _warmupMinSubtreeBytes;
    private readonly PbtPrefixlessBranchOmission _prefixlessBranchOmission;
    private readonly IRefCountingMemoryProvider _nodeGroupMemory;
    private readonly IPbtCommitTarget _commitTarget;
    private readonly IPbtChildHeaderSource _childHeaders;
    private readonly bool _isReadOnly;
    private readonly ITrieWarmer _trieWarmer;
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];
    private readonly object _warmupLock = new();

    private StateId _currentStateId;
    private Hash256 _rootHash;
    private ValueHash256 _treeRoot;
    private Hash256? _authoritativeRoot;
    private BlockHeader? _currentHeader;
    private BlockHeader? _childHeader;
    private bool _rootDirty;
    private bool _isDisposed;
    private bool _pausePrewarmer;
    private int _hintSequenceId;
    private PbtTrieWarmupSession? _warmupSession;

    public PbtWorldStateScope(
        in StateId currentStateId,
        BlockHeader? currentHeader,
        PbtSnapshotBundle bundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IPbtCommitTarget commitTarget,
        IPbtChildHeaderSource childHeaders,
        IPbtResourcePool resourcePool,
        PbtResourcePool.Usage usage,
        bool isReadOnly,
        ITrieWarmer trieWarmer,
        IPbtConfig config,
        ILogManager? logManager = null)
    {
        _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<PbtWorldStateScope>();
        _foldQuota = new ConcurrencyController(config.FoldConcurrency > 0 ? config.FoldConcurrency : Environment.ProcessorCount);
        _foldFanOut = new(config.FoldMinOperationsPerWorker, config.FoldLargeSubtreeBytes, config.FoldLargeSubtreeMinOperationsPerWorker);
        _warmupMinSubtreeBytes = config.WarmupMinSubtreeBytes;
        _prefixlessBranchOmission = config.PrefixlessBranchOmission;
        _nodeGroupMemory = resourcePool.NodeGroupMemory;
        _usage = usage;
        _currentStateId = currentStateId;
        _currentHeader = currentHeader;
        Bundle = bundle;
        _commitTarget = commitTarget;
        _childHeaders = childHeaders;
        _isReadOnly = isReadOnly;
        _trieWarmer = trieWarmer;
        _treeRoot = bundle.TreeRoot;
        _rootHash = currentStateId.StateRoot.ToHash256();
        CodeDb = new PbtCodeDb(codeDb, Bundle);
        _trieWarmer.OnEnterScope();
        if (_logger.IsDebug) LogLifecycle("opened");
    }

    private void LogLifecycle(string stage) =>
        _logger.Debug($"PBT scope {_scopeId} {stage}: state={_currentStateId}, usage={_usage}, readOnly={_isReadOnly}, pendingMutations={Bundle.PendingMutationCount}, managedBytes={GC.GetTotalMemory(false)}");

    internal PbtSnapshotBundle Bundle { get; }
    public Hash256 RootHash => _rootHash;
    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    internal void UseAuthoritativeRoot(Hash256 root)
    {
        _authoritativeRoot = root;
        _rootHash = root;
    }

    // A read rewrites nothing at commit, so its trie path needs no warming; changes hint through HintWarmAccount.
    public Account? Get(Address address) => Bundle.GetAccount(address);

    public void HintGet(Address address, Account? account) => Bundle.HintAccount(address, account);

    public void HintWarmAccount(in ValueAddress address)
    {
        using IWorldStateScopeProvider.ITrieWarmupSession session = CreateTrieWarmupSession();
        session.HintWarmAccount(in address);
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index)
    {
        using IWorldStateScopeProvider.ITrieWarmupSession session = CreateTrieWarmupSession();
        session.HintWarmSlot(in address, in index);
    }

    internal void HintSet(Address address, in UInt256 index)
    {
        using IWorldStateScopeProvider.ITrieWarmupSession session = CreateTrieWarmupSession();
        if (session is PbtTrieWarmupSession pbtSession) pbtSession.HintWarmSlot(new ValueAddress(address.Bytes), address, in index, singleProducer: true);
    }

    /// <inheritdoc/>
    public IWorldStateScopeProvider.ITrieWarmupSession CreateTrieWarmupSession()
    {
        if (_trieWarmer is NoopTrieWarmer) return IWorldStateScopeProvider.ITrieWarmupSession.Noop.Instance;
        // A live session is leased without the lock; a lease landing while the session is retired is harmless
        // because a stopped session ignores hints and its cleanup waits for the last lease.
        PbtTrieWarmupSession? session = Volatile.Read(ref _warmupSession);
        if (session is not null && session.TryAcquireLease()) return session;
        lock (_warmupLock)
        {
            if (_isDisposed || _pausePrewarmer)
                return IWorldStateScopeProvider.ITrieWarmupSession.Noop.Instance;
            _warmupSession ??= Bundle.CreateTrieWarmupSession(_trieWarmer, _hintSequenceId, _warmupMinSubtreeBytes, _logger);
            _warmupSession.AcquireLease();
            return _warmupSession;
        }
    }

    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => GetOrCreateStorageTree(address);

    private PbtStorageTree GetOrCreateStorageTree(Address address)
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
        if (!_rootDirty) return;
        // Warming past this point cannot land before the fold reads the path, so the pending jobs are cut off here.
        RetireWarmupSession();
        try
        {
            FoldRoot();
        }
        finally
        {
            ResumePrewarmer();
        }
    }

    private void FoldRoot()
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
                _treeRoot = TrieUpdater.UpdateRoot(store, _treeRoot, changes, _foldQuota, _foldFanOut, _prefixlessBranchOmission, Metrics.PbtPartitionFoldTime, memoryProvider: _nodeGroupMemory);
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

    public void Commit(ulong blockNumber)
    {
        if (_logger.IsDebug) LogLifecycle($"commit begin block={blockNumber}");
        RetireWarmupSession();
        try
        {
            FoldRoot();
            StateId newStateId = new(blockNumber, _rootHash);
            if (newStateId != _currentStateId)
            {
                PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _treeRoot, out PbtTransientResource transientResource);
                if (_isReadOnly)
                {
                    snapshot.Dispose();
                    transientResource.ReleaseLease();
                }
                else _commitTarget.AddSnapshot(snapshot, transientResource);
                _currentStateId = newStateId;
            }
            _currentHeader = _childHeader;
            _childHeader = null;
            lock (_storages) _storages.Clear();
            _rootDirty = false;
            if (_logger.IsDebug) LogLifecycle($"commit completed block={blockNumber}");
        }
        finally
        {
            ResumePrewarmer();
        }
    }

    private void RetireWarmupSession()
    {
        PbtTrieWarmupSession? session;
        lock (_warmupLock)
        {
            _pausePrewarmer = true;
            _hintSequenceId++;
            session = _warmupSession;
            _warmupSession = null;
        }
        if (session is null) return;
        session.StopWarming();
        session.Dispose();
    }

    private void ResumePrewarmer()
    {
        lock (_warmupLock) _pausePrewarmer = false;
    }

    public void Dispose()
    {
        lock (_warmupLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (_logger.IsDebug) LogLifecycle("close begin");
        }
        try
        {
            try
            {
                RetireWarmupSession();
            }
            finally
            {
                Bundle.Dispose();
            }
        }
        finally
        {
            _trieWarmer.OnExitScope();
            if (_logger.IsDebug) _logger.Debug($"PBT scope {_scopeId} closed: state={_currentStateId}, usage={_usage}, managedBytes={GC.GetTotalMemory(false)}");
        }
    }

    private sealed class WriteBatch(PbtWorldStateScope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated { add { } remove { } }

        public void Set(Address key, Account? account)
        {
            scope.Bundle.SetAccount(key, account);
            scope._rootDirty = true;
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(scope, key);

        public void Dispose()
        {
            // A tree created before these writes may have cached an emptiness that they just ended.
            lock (scope._storages) scope._storages.Clear();
            Metrics.PbtWriteBatchTime.Observe(Stopwatch.GetTimestamp() - _start);
        }
    }

    private sealed class StorageWriteBatch(PbtWorldStateScope scope, Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        // One batch serves one contract on one thread, so the address hash is derived once for the whole run of slots.
        private readonly ValueHash256 _addressHash = PbtKeyDerivation.AddressKeyHash(address);

        public void Set(in UInt256 index, in UInt256 value)
        {
            scope.Bundle.SetSlot(address, _addressHash, index, EvmWordSlot.FromUInt256(in value));
            scope._rootDirty = true;
        }

        public void Clear()
        {
            scope.Bundle.SelfDestruct(_addressHash);
            scope._rootDirty = true;
        }

        public void Dispose() { }
    }
}
