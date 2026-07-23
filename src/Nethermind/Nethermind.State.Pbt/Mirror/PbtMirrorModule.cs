// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Steps;

namespace Nethermind.State.Pbt.Mirror;

/// <summary>
/// Wires the PBT backend as a shadow of the flat one rather than in place of it: only PBT's storage
/// half is registered, plus the two decorators that tie it to the flat backend — the main-processing
/// scope provider, and the persistence that drives it.
/// </summary>
/// <remarks>
/// Nothing the world-state decider selects is overridden, so the flat backend remains the node's state
/// backend in every respect: its manager, state reader, snap server, state boundary and sync store are
/// untouched, and only main block processing is mirrored. See <see cref="IPbtConfig.MirrorFlat"/>.
/// </remarks>
public class PbtMirrorModule(IPbtConfig config) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddColumnDatabase<PbtColumns>(DbNames.Pbt)
            .AddSingleton<IPbtPersistence, PbtRocksDbPersistence>()
            // singleton: a second pool would silently halve every hit rate
            .AddSingleton<IPbtResourcePool, PbtResourcePool>()
            .AddSingleton<PbtSnapshotRepository>()
            .AddSingleton<PbtSnapshotCompactor>()
            .AddSingleton<PbtCompactionSchedule>()
            .AddSingleton<PbtPersistenceCoordinator>()
            // the concrete manager is what the persistence decorator drives, and both registrations
            // must resolve the same instance - it owns the layer repository and the background workers
            .AddSingleton<PbtDbManager>()
            .Bind<IPbtDbManager, PbtDbManager>()

            .AddDecorator<IPersistence, PbtFlatDrivenPersistence>()
            .AddSingleton<IMainProcessingModule, PbtMirrorMainProcessingModule>();

        // the source flat db is the one the node already runs on, so unlike the standalone module
        // nothing extra has to be opened for the import; it exits the process when it is done
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
