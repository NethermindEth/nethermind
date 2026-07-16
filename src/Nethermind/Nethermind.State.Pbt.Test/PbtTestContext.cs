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
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Pbt.Test;

/// <summary>Wires the full PBT component stack over in-memory column dbs, as the plugin module would.</summary>
internal sealed class PbtTestContext : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public SnapshotableMemColumnsDb<PbtColumns> Db { get; }
    public MemDb CodeDb { get; } = new();
    public PbtConfig Config { get; }
    public TestFinalizedStateProvider FinalizedStateProvider { get; } = new();
    public PbtSnapshotRepository Repository { get; } = new();
    public IPbtResourcePool ResourcePool { get; }
    public PbtRocksDbPersistence Persistence { get; }
    public PbtPersistenceCoordinator Coordinator { get; }
    public PbtDbManager Manager { get; }
    public PbtStateReader StateReader { get; }
    public PbtWorldStateManager WorldStateManager { get; }

    public PbtTestContext(SnapshotableMemColumnsDb<PbtColumns>? db = null, PbtConfig? config = null)
    {
        Db = db ?? new SnapshotableMemColumnsDb<PbtColumns>("pbt");
        Config = config ?? new PbtConfig();
        Persistence = new PbtRocksDbPersistence(Db);
        ResourcePool = new PbtResourcePool(Config);
        Coordinator = new PbtPersistenceCoordinator(Config, FinalizedStateProvider, Persistence, Repository, new PbtSnapshotCompactor(ResourcePool), NullStatePersistenceBarrier.Instance, LimboLogs.Instance);
        Manager = new PbtDbManager(Repository, Coordinator, Persistence, ResourcePool, new TestProcessExitSource(_cts), LimboLogs.Instance);
        StateReader = new PbtStateReader(CodeDb, Manager);
        WorldStateManager = new PbtWorldStateManager(Manager, StateReader, () => new PbtOverridableWorldScope(CodeDb, Manager, ResourcePool), CodeDb);
    }

    public PbtScopeProvider CreateScopeProvider(bool isReadOnly = false) =>
        new(CodeDb, Manager, isReadOnly ? PbtResourcePool.Usage.ReadOnlyProcessingEnv : PbtResourcePool.Usage.MainBlockProcessing, isReadOnly);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Manager.DisposeAsync();
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
