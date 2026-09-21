// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Blockchain;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Steps;
using Nethermind.State.Pbt.Sync;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Runs the native flat and PBT backends side by side and switches between them at EIP-8347 activation.</summary>
/// <remarks>
/// The flat graph is always registered by the core modules; this module adds the native PBT graph (as
/// <see cref="PbtModule"/> does) and binds the composite world state over both. Only flat's persistence sees the
/// clamped finality (<see cref="MigrationFlatFinalizedStateProvider"/>); PBT persists on the real one.
/// </remarks>
internal sealed class PbtMigrationModule(IPbtConfig configuration) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddColumnDatabase<PbtColumns>(DbNames.Pbt)
            .AddDecorator<IRocksDbConfigFactory, PbtRocksDbConfigAdjuster>()
            .AddSingleton<IPbtPersistence, PbtRocksDbPersistence>()
            .AddDecorator<IPbtPersistence, PbtCachedReaderPersistence>()
            .AddSingleton<IPbtResourcePool, PbtResourcePool>()
            .AddSingleton<PbtTrieNodeCache>()
            .Bind<IPbtTrieNodeCache, PbtTrieNodeCache>()
            .AddSingleton<PbtSnapshotRepository>()
            .AddSingleton<PbtSnapshotCompactor>()
            .AddSingleton<PbtCompactionSchedule>()
            .AddSingleton<PbtPersistenceCoordinator>()
            .AddSingleton<IPbtDbManager, PbtDbManager>()
            .AddSingleton<PbtStateReader>()
            .AddSingleton<PbtWorldStateManager>()
            .Add<PbtOverridableWorldScope>()
            .AddSingleton<IPbtChildHeaderSource>(NullPbtChildHeaderSource.Instance)

            .AddSingleton<MigrationBackendSelector>()
            .AddSingleton<MigrationScopeProvider>()
            .AddSingleton<MigrationStateReader>()
            .Add<MigrationOverridableWorldScope>()
            .AddSingleton<MigrationWorldStateManager>()
            .Bind<IWorldStateManager, MigrationWorldStateManager>()

            .AddSingleton<MigrationStateBoundary>()
            .Bind<IStateBoundary, MigrationStateBoundary>()
            .Bind<IFullStateFinder, MigrationStateBoundary>()
            .AddSingleton<ISnapTrieFactory, PbtUnsupportedSnapTrieFactory>()
            .AddSingleton<ITreeSyncStore, PbtUnsupportedTreeSyncStore>()
            .AddSingleton<IBalHealing>(NoopBalHealing.Instance)
            .AddSingleton<IPruningTrieStateAdminRpcModule, MigrationPruningDisabled>()

            .AddSingleton<MigrationFlatFinalizedStateProvider>()
            .AddSingleton<PbtAnchorPublication>()
            .AddSingleton<PbtMigrationBootstrap>()
            .AddSingleton<PbtBalReplay>()
            .AddSingleton<PbtBalFollower>()
            .AddSingleton<PbtBalFollowerScheduler>()
            .AddSingleton<MerkleShadowFollower>()
            .Bind<IMerkleShadowFollower, MerkleShadowFollower>()
            .AddSingleton<IMigrationTelemetry, MigrationTelemetry>()
            .AddStep(typeof(InitializePbtMigration));

        // Flat's persistence must not seek post-activation boundaries, whose canonical roots are PBT roots.
        builder.RegisterType<PersistenceManager>().As<IPersistenceManager>().SingleInstance()
            .WithParameter(new ResolvedParameter(
                (parameter, _) => parameter.ParameterType == typeof(IStateHeaderProvider),
                (_, context) => context.Resolve<MigrationFlatFinalizedStateProvider>()));

        if (configuration.MigrationGenesisBootstrap)
            builder.AddSingleton<MigrationGenesisSource>().AddSingleton<MigrationGenesisBootstrap>()
                .Bind<IGenesisPostProcessor, MigrationGenesisBootstrap>();
    }

    private sealed class MigrationPruningDisabled : IPruningTrieStateAdminRpcModule
    {
        public ResultWrapper<PruningStatus> admin_prune() => ResultWrapper<PruningStatus>.Success(PruningStatus.Disabled);
    }
}
