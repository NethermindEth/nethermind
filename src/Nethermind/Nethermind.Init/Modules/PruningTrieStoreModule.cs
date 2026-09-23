// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Healing;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.Trie;
using Nethermind.Trie;

namespace Nethermind.Init.Modules;

public class PruningTrieStoreModule : Module
{
    protected override void Load(ContainerBuilder builder) =>
        builder

            // Special case for state db with pruning trie state.
            .AddKeyedSingleton<IDb>(DbNames.State, (ctx) =>
            {
                IFileSystem fileSystem = ctx.Resolve<IFileSystem>();
                IDbFactory dbFactory = ctx.Resolve<IDbFactory>();
                DbSettings stateDbSettings = new(GetTitleDbName(DbNames.State), DbNames.State);
                stateDbSettings.DeleteOnStart = ShouldDropPruningTrieState(
                    ctx.ResolveOptional<IFlatDbConfig>(), ctx.Resolve<IPersistence>, ctx.Resolve<ILogManager>,
                    () => HasSstFiles(fileSystem, dbFactory.GetFullDbPath(stateDbSettings)));
                IDbFactory innerDbFactory = dbFactory;
                if (dbFactory is not MemDbFactory)
                {
                    FullPruningInnerDbFactory fullPruningInnerDbFactory = new(dbFactory, fileSystem, stateDbSettings.DbPath);
                    // DeleteOnStart reaches only the inner DB that gets opened. A copy left by an interrupted
                    // full pruning would otherwise stay on disk for good: only the next pruning clears it,
                    // and a flat node never runs one.
                    if (stateDbSettings.DeleteOnStart) DeleteStaleInnerDbs(fullPruningInnerDbFactory, ctx.Resolve<ILogManager>());
                    innerDbFactory = fullPruningInnerDbFactory;
                }

                FullPruningDb db = new(
                    stateDbSettings,
                    innerDbFactory,
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

            // The trie backend's IStateBoundary. Registered here (not off IWorldStateManager) so it
            // can be injected into the block tree, whose constructor runs before the manager graph.
            .AddSingleton<StateBoundaryStore>(ctx =>
            {
                IPruningConfig pruningConfig = ctx.Resolve<IPruningConfig>();
                ulong? retentionWindowBlocks = pruningConfig.Mode.IsMemory() ? pruningConfig.PruningBoundary : null;
                return new StateBoundaryStore(
                    ctx.ResolveKeyed<IDb>(DbNames.State),
                    ctx.ResolveKeyed<IDb>(DbNames.BlockInfos),
                    retentionWindowBlocks,
                    ctx.Resolve<ILogManager>());
            })
            .Map<IStateBoundaryWriter, StateBoundaryStore>((store) => store)

            // Sync components backed by the patricia trie store
            .AddSingleton<FullStateFinder>()
            .AddSingleton<PatriciaSnapTrieFactory>()
            .AddSingleton<PatriciaTreeSyncStore>()
            .AddSingleton<IPathRecovery, ISyncPeerPool, INodeStorage, ILogManager>((peerPool, nodeStorage, logManager) => new PathNodeRecovery(
                new NodeDataRecovery(peerPool!, nodeStorage, logManager),
                new SnapRangeRecovery(peerPool!, logManager),
                logManager
            ))
            .AddSingleton<ICodeRecovery, CodeRecovery>()
            ;

    /// <summary>Whether to wipe the patricia-trie state DB as it is opened.</summary>
    /// <remarks>
    /// Not decided from <see cref="FlatStateActivationPolicy"/>, which depends on this database. The checks
    /// below are a strict subset of it, so this never wipes a DB the node is about to run on.
    /// </remarks>
    /// <param name="hasTrieData">Whether the trie store still holds data. Decides the log level only: the
    /// deletion stays unconditional so that a deletion interrupted by a crash completes on the next start.</param>
    internal static bool ShouldDropPruningTrieState(IFlatDbConfig? flatDbConfig, Func<IPersistence> flatPersistence, Func<ILogManager> logManager, Func<bool> hasTrieData)
    {
        // Null when nothing registered the flat config: the state DB must still resolve, so nothing
        // beyond the flag may be resolved until the flag is known to be set.
        if (flatDbConfig is not { DropPruningTrieState: true }) return false;

        // The flag is opt-in, so every decline says why.
        ILogger logger = logManager().GetClassLogger<PruningTrieStoreModule>();
        if (!flatDbConfig.Enabled)
        {
            if (logger.IsInfo) logger.Info("Keeping the patricia trie state: the flat DB is disabled, so the node runs on the patricia backend.");
            return false;
        }

        // ImportFallbackStateBoundary only reads the trie's BestPersistedState while the flat one is
        // null, which is exactly StateId.PreGenesis - the case the next check rejects. So a populated
        // flat store is sufficient on its own, and the import flag needs no gate of its own.
        using IPersistence.IPersistenceReader reader = flatPersistence().CreateReader();
        if (reader.CurrentState == StateId.PreGenesis)
        {
            if (logger.IsInfo) logger.Info("Keeping the patricia trie state: the flat DB is empty, so the node would be left without any state.");
            return false;
        }

        if (hasTrieData())
        {
            if (logger.IsWarn) logger.Warn($"Dropping the patricia trie state DB: the flat DB owns the state at {reader.CurrentState}. This is irreversible - a switch back to the patricia backend will require a resync.");
        }
        else if (logger.IsDebug) logger.Debug("Dropping the empty patricia trie state DB.");

        return true;
    }

    private static bool HasSstFiles(IFileSystem fileSystem, string path)
    {
        try
        {
            return fileSystem.Directory.Exists(path)
                   // AllDirectories covers the indexed state/0 layout as well as the legacy main-directory one.
                   && fileSystem.Directory.EnumerateFiles(path, "*.sst", SearchOption.AllDirectories).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only the log level depends on this, so it must not abort startup. Cannot tell: warn rather
            // than drop a possibly populated store quietly.
            return true;
        }
    }

    private static void DeleteStaleInnerDbs(FullPruningInnerDbFactory innerDbFactory, ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger<PruningTrieStoreModule>();
        try
        {
            int deleted = innerDbFactory.DeleteStaleInnerDbs();
            if (deleted > 0 && logger.IsInfo) logger.Info($"Deleted {deleted} leftover full-pruning {(deleted == 1 ? "copy" : "copies")} of the patricia trie state DB.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (logger.IsWarn) logger.Warn($"Could not delete the leftover full-pruning copies of the patricia trie state DB. {e.Message}");
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
