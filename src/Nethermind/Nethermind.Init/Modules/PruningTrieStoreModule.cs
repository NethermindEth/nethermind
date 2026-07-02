// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.PartialArchive;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.State;
using Nethermind.State.Healing;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.Trie;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Init.Modules;

public class PruningTrieStoreModule(IInitConfig initConfig, ISyncConfig syncConfig, INetworkConfig networkConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (syncConfig.PartialArchiveEnabled)
        {
            builder
                .AddSingleton<PartialArchiveNodeTracker>()
                .Bind<IPersistedNodeObserver, PartialArchiveNodeTracker>();

            if (!string.IsNullOrEmpty(syncConfig.PartialArchiveFeeders))
            {
                // Feeders must be persistent connections so the fast-fill pivot probe can reach them.
                networkConfig.StaticPeers = string.IsNullOrEmpty(networkConfig.StaticPeers)
                    ? syncConfig.PartialArchiveFeeders
                    : $"{networkConfig.StaticPeers},{syncConfig.PartialArchiveFeeders}";
            }
        }

        builder

            // Special case for state db with pruning trie state.
            .AddKeyedSingleton<IDb>(DbNames.State, (ctx) =>
            {
                DbSettings stateDbSettings = new(GetTitleDbName(DbNames.State), DbNames.State);
                IFileSystem fileSystem = ctx.Resolve<IFileSystem>();
                IDbFactory dbFactory = ctx.Resolve<IDbFactory>();
                FullPruningDb db = new(
                    stateDbSettings,
                    dbFactory is not MemDbFactory
                        ? new FullPruningInnerDbFactory(dbFactory, fileSystem, stateDbSettings.DbPath)
                        : dbFactory,
                    () => Interlocked.Increment(ref Nethermind.Db.Metrics.StateDbInPruningWrites));
                // Register the outer wrapper so GatherMetric() always reflects the currently active
                // inner DB, even across full-pruning cycles. The inner DBs are not tracked:
                // - via FullPruningInnerDbFactory they get SkipMetricsTracking = true so the
                //   DbFactoryInterceptor skips registration.
                // - via the MemDbFactory branch they're MemDbs created outside any interceptor and
                //   therefore never reach the tracker either.
                ctx.ResolveOptional<DbMonitoringModule.DbTracker>()?.AddDb(stateDbSettings.DbName, db);
                return db;
            })

            .AddSingleton<INodeStorageFactory>(ctx =>
            {
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                ISyncConfig syncConfig = ctx.Resolve<ISyncConfig>();
                IDb stateDb = ctx.ResolveKeyed<IDb>(DbNames.State);
                ILogManager logManager = ctx.Resolve<ILogManager>();
                INodeStorageFactory nodeStorageFactory = new NodeStorageFactory(initConfig.StateDbKeyScheme, logManager);
                nodeStorageFactory.DetectCurrentKeySchemeFrom(stateDb);

                syncConfig.SnapServingEnabled |= syncConfig.SnapServingEnabled is null
                                                 && nodeStorageFactory.CurrentKeyScheme is INodeStorage.KeyScheme.HalfPath or null
                                                 && initConfig.StateDbKeyScheme != INodeStorage.KeyScheme.Hash;

                if (nodeStorageFactory.CurrentKeyScheme is INodeStorage.KeyScheme.Hash
                    || initConfig.StateDbKeyScheme == INodeStorage.KeyScheme.Hash)
                {
                    // Special case in case its using hashdb, use a slightly different database configuration.
                    if (stateDb is ITunableDb tunableDb) tunableDb.Tune(ITunableDb.TuneType.HashDb);
                }

                return nodeStorageFactory;
            })

            // Used by sync code and trie store
            .AddSingleton<INodeStorage>(ctx =>
            {
                IDb stateDb = ctx.ResolveKeyed<IDb>(DbNames.State);
                INodeStorageFactory nodeStorageFactory = ctx.Resolve<INodeStorageFactory>();
                return nodeStorageFactory.WrapKeyValueStore(stateDb);
            })

            // Most config actually done in factory. We just call `Build` and then get back components from its output.
            .AddSingleton<MainPruningTrieStoreFactory>() // This part is done separately so that triestore can be obtained in test.
            .AddSingleton<CompositePruningTrigger>()
            .AddSingleton<IFullPrunerFactory, FullPrunerFactory>()
            .AddSingleton<PruningTrieStateFactory>()
            .AddSingleton<PruningTrieStateFactoryOutput>()

            // IStateBoundaryWriter is trie-specific (flat tracks state via PersistenceManager directly).
            // Mapped from the trie factory output so it stays unresolved when flat is active.
            .Map<IStateBoundaryWriter, PruningTrieStateFactoryOutput>((o) => (IStateBoundaryWriter)o.WorldStateManager)

            // Sync components backed by the patricia trie store
            .AddSingleton<FullStateFinder>()
            .AddSingleton<PatriciaSnapTrieFactory>()
            .AddSingleton<PatriciaTreeSyncStore>()
            .AddSingleton<IPathRecovery, ISyncPeerPool, INodeStorage, ILogManager>((peerPool, nodeStorage, logManager) => new PathNodeRecovery(
                new NodeDataRecovery(peerPool!, nodeStorage, logManager),
                new SnapRangeRecovery(peerPool!, logManager),
                logManager
            ))
            ;

        if (initConfig.DiagnosticMode == DiagnosticMode.VerifyTrie)
        {
            builder.AddStep(typeof(RunVerifyTrie));
        }
    }

    private static string GetTitleDbName(string dbName) => char.ToUpper(dbName[0]) + dbName[1..];

    // Just a wrapper to easily extract the output of `PruningTrieStateFactory` which do the actual initializations.
    internal class PruningTrieStateFactoryOutput
    {
        public IWorldStateManager WorldStateManager { get; }
        public IPruningTrieStateAdminRpcModule AdminRpcModule { get; }

        public PruningTrieStateFactoryOutput(PruningTrieStateFactory factory)
        {
            (IWorldStateManager worldStateManager, IPruningTrieStateAdminRpcModule adminRpc) = factory.Build();
            WorldStateManager = worldStateManager;
            AdminRpcModule = adminRpc;
        }
    }
}
