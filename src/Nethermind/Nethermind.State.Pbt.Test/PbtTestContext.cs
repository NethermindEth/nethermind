// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.State.Pbt.Test;

/// <summary>Wires the full PBT component stack over in-memory column dbs, as the plugin module would.</summary>
internal sealed class PbtTestContext : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly PbtCachedReaderPersistence _cachedReaderPersistence;
    private readonly PbtTrieNodeCache _trieNodeCache;

    public IColumnsDb<PbtColumns> Db { get; }
    public MemDb CodeDb { get; } = new();
    public PbtConfig Config { get; }
    public TestFinalizedStateProvider FinalizedStateProvider { get; } = new();
    public TestStateHeaderProvider StateHeaderProvider { get; } = new();
    public PbtSnapshotRepository Repository { get; } = new();
    public IPbtResourcePool ResourcePool { get; }
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

    public PbtTestContext(IColumnsDb<PbtColumns>? db = null, PbtConfig? config = null, IPbtChildHeaderSource? childHeaders = null, ITrieWarmer? trieWarmer = null, IMetricsConfig? metricsConfig = null)
    {
        metricsConfig ??= new MetricsConfig();
        Db = db ?? new SnapshotableMemColumnsDb<PbtColumns>("pbt");
        Config = config ?? new PbtConfig();
        ChildHeaders = childHeaders ?? NullPbtChildHeaderSource.Instance;
        TrieWarmer = trieWarmer ?? new NoopTrieWarmer();

        // Production randomizes this offset; tests must use stable compaction boundaries.
        if (Config.CompactionOffset < 0) Config.CompactionOffset = 0;
        _cachedReaderPersistence = new PbtCachedReaderPersistence(new PbtRocksDbPersistence(Db, new PbtConfig()), new TestProcessExitSource(_cts));
        Persistence = _cachedReaderPersistence;
        ResourcePool = new PbtResourcePool(Config);
        Schedule = new PbtCompactionSchedule(MetadataDb, Config, LimboLogs.Instance);
        Compactor = new PbtSnapshotCompactor(ResourcePool, Schedule, Repository, Config);
        Coordinator = new PbtPersistenceCoordinator(Config, FinalizedStateProvider, Persistence, Repository, Schedule, NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        _trieNodeCache = new PbtTrieNodeCache(Config);
        Manager = new PbtDbManager(Repository, Coordinator, Persistence, ResourcePool, Compactor, new TestProcessExitSource(_cts), LimboLogs.Instance, Config, metricsConfig, _trieNodeCache);
        StateReader = new PbtStateReader(CodeDb, Manager);
        WorldStateManager = new PbtWorldStateManager(Manager, ChildHeaders, StateHeaderProvider, ResourcePool, StateReader, () => new PbtOverridableWorldScope(CodeDb, Manager, ResourcePool, metricsConfig, Config, StateHeaderProvider, _trieNodeCache), TrieWarmer, CodeDb, Config);
    }

    public PbtScopeProvider CreateScopeProvider(bool isReadOnly = false, ILogManager? logManager = null) =>
        new(CodeDb, Manager, ChildHeaders, StateHeaderProvider, ResourcePool, isReadOnly ? PbtResourcePool.Usage.ReadOnlyProcessingEnv : PbtResourcePool.Usage.MainBlockProcessing, isReadOnly,
            TrieWarmer, Config, logManager);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Manager.DisposeAsync();
        _trieNodeCache.Dispose();
        await _cachedReaderPersistence.DisposeAsync();
        _cts.Dispose();
    }

    /// <summary>Finality as the persistence coordinator sees it; parents are never resolved through it.</summary>
    public sealed class TestFinalizedStateProvider : IStateHeaderProvider
    {
        private readonly Dictionary<ulong, BlockHeader> _headers = [];

        public ulong FinalizedBlockNumber { get; set; }

        public BlockHeader? GetFinalizedHeader(ulong blockNumber) => _headers.GetValueOrDefault(blockNumber);

        public BlockHeader? FindParentHeader(BlockHeader target) => null;

        public void SetCanonicalRoot(ulong blockNumber, Hash256 root) =>
            _headers[blockNumber] = Build.A.BlockHeader.WithNumber(blockNumber).WithStateRoot(root).TestObject;
    }

    private sealed class TestProcessExitSource(CancellationTokenSource cts) : IProcessExitSource
    {
        public CancellationToken Token => cts.Token;

        public void Exit(int exitCode) => throw new NotSupportedException();
    }
}
