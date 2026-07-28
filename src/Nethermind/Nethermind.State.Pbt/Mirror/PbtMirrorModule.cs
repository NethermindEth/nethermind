// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Steps;

namespace Nethermind.State.Pbt.Mirror;

/// <summary>Registers PBT as a mirror of the flat backend.</summary>
/// <remarks>
/// The flat backend remains authoritative; only main block processing is mirrored. See
/// <see cref="IPbtConfig.MirrorFlat"/>.
/// </remarks>
public class PbtMirrorModule(IPbtConfig config) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddColumnDatabase<PbtColumns>(DbNames.Pbt)
            .AddDecorator<IRocksDbConfigFactory, PbtRocksDbConfigAdjuster>()
            .AddSingleton<IPbtPersistence, PbtRocksDbPersistence>()
            .AddDecorator<IPbtPersistence, PbtCachedReaderPersistence>()
            // A second pool would halve cache hit rates.
            .AddSingleton<IPbtResourcePool, PbtResourcePool>()
            .AddSingleton<PbtStoreCache>()
            .AddSingleton<PbtSnapshotRepository>()
            .AddSingleton<PbtSnapshotCompactor>()
            .AddSingleton<PbtCompactionSchedule>()
            .AddSingleton<PbtPersistenceCoordinator>()
            // Both registrations must share the manager that owns the layer repository and workers.
            .AddSingleton<PbtDbManager>()
            .Bind<IPbtDbManager, PbtDbManager>()

            .AddDecorator<IPersistence, PbtFlatDrivenPersistence>()
            .AddSingleton<IMainProcessingModule, PbtMirrorMainProcessingModule>();

        if (config.ImportFromPreimageFlat)
        {
            builder
                .AddSingleton<PbtRebuilder>()
                .AddStep(typeof(ImportPbtFromPreimageFlat));
        }
        else
        {
            builder.AddStep(typeof(VerifyPbtMirrorAlignment));
        }
    }

    private sealed class PbtMirrorMainProcessingModule : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) =>
            builder
                .AddDecorator<IWorldStateScopeProvider>((ctx, worldStateScopeProvider) =>
                    worldStateScopeProvider is PbtMirrorScopeProvider
                        ? worldStateScopeProvider
                        : new PbtMirrorScopeProvider(
                            worldStateScopeProvider,
                            ctx.Resolve<IPbtDbManager>(),
                            ctx.Resolve<IPbtResourcePool>(),
                            ctx.Resolve<IPbtConfig>()));
    }
}
