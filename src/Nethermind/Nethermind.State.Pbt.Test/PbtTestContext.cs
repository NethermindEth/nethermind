// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt.Test;

/// <summary>Wires the full PBT component stack over in-memory column dbs, as the plugin module would.</summary>
internal sealed class PbtTestContext : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly PbtCachedReaderPersistence _cachedReaderPersistence;

    public SnapshotableMemColumnsDb<PbtColumns> Db { get; }
    public MemDb CodeDb { get; } = new();
    public PbtConfig Config { get; }
    public TestFinalizedStateProvider FinalizedStateProvider { get; } = new();
    public PbtSnapshotRepository Repository { get; } = new();
    public IPbtResourcePool ResourcePool { get; }
    public PbtStoreCache StoreCache { get; }
    public IDb MetadataDb { get; } = new MemDb();
    public PbtCompactionSchedule Schedule { get; }
    public PbtSnapshotCompactor Compactor { get; }
    public IPbtPersistence Persistence { get; }
    public PbtPersistenceCoordinator Coordinator { get; }
    public PbtDbManager Manager { get; }
    public PbtStateReader StateReader { get; }
    public PbtWorldStateManager WorldStateManager { get; }
    public ITrieWarmer TrieWarmer { get; }

    /// <summary>Resolves nothing unless a test supplies one, so scopes report their own EIP-8297 root.</summary>
    public IPbtChildHeaderSource ChildHeaders { get; }

    public PbtTestContext(SnapshotableMemColumnsDb<PbtColumns>? db = null, PbtConfig? config = null, IPbtChildHeaderSource? childHeaders = null, ITrieWarmer? trieWarmer = null)
    {
        Db = db ?? new SnapshotableMemColumnsDb<PbtColumns>("pbt");
        Config = config ?? new PbtConfig();
        ChildHeaders = childHeaders ?? NullPbtChildHeaderSource.Instance;
        TrieWarmer = trieWarmer ?? new NoopTrieWarmer();

        // Production randomizes this offset; tests must use stable compaction boundaries.
        if (Config.CompactionOffset < 0) Config.CompactionOffset = 0;
        _cachedReaderPersistence = new PbtCachedReaderPersistence(new PbtRocksDbPersistence(Db, new PbtConfig()), new TestProcessExitSource(_cts));
        Persistence = _cachedReaderPersistence;
        ResourcePool = new PbtResourcePool(Config);
        StoreCache = new PbtStoreCache(Config);
        Schedule = new PbtCompactionSchedule(MetadataDb, Config, LimboLogs.Instance);
        Compactor = new PbtSnapshotCompactor(ResourcePool, Schedule, Repository, Config);
        Coordinator = new PbtPersistenceCoordinator(Config, FinalizedStateProvider, Persistence, Repository, Compactor, Schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        Manager = new PbtDbManager(Repository, Coordinator, Persistence, ResourcePool, Compactor, new TestProcessExitSource(_cts), LimboLogs.Instance);
        StateReader = new PbtStateReader(CodeDb, Manager);
        WorldStateManager = new PbtWorldStateManager(Manager, ChildHeaders, ResourcePool, StateReader, () => new PbtOverridableWorldScope(CodeDb, Manager, ResourcePool, StoreCache, Config, new MetricsConfig()), Config, TrieWarmer, CodeDb);
    }

    public PbtScopeProvider CreateScopeProvider(bool isReadOnly = false) =>
        new(CodeDb, Manager, ChildHeaders, ResourcePool, isReadOnly ? PbtResourcePool.Usage.ReadOnlyProcessingEnv : PbtResourcePool.Usage.MainBlockProcessing, isReadOnly,
            Config.TrieNodeLayout, Config.RootFoldConcurrency, TrieWarmer);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Manager.DisposeAsync();
        await _cachedReaderPersistence.DisposeAsync();
        StoreCache.Dispose();
        _cts.Dispose();
    }

    public sealed class TestFinalizedStateProvider : IFinalizedStateProvider
    {
        private readonly Dictionary<ulong, Hash256> _roots = [];

        public ulong FinalizedBlockNumber { get; set; }

        public Hash256? GetFinalizedStateRootAt(ulong blockNumber) => _roots.GetValueOrDefault(blockNumber);

        public void SetCanonicalRoot(ulong blockNumber, Hash256 root) => _roots[blockNumber] = root;
    }

    private sealed class TestProcessExitSource(CancellationTokenSource cts) : IProcessExitSource
    {
        public CancellationToken Token => cts.Token;

        public void Exit(int exitCode) => throw new NotSupportedException();
    }
}
