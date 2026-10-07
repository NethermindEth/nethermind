// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Container;
using Nethermind.Core.Memory;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.JsonRpc.Modules;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Migration;
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
            .AddPbtCore(config)
            .AddDecorator<IPersistence, PbtFlatDrivenPersistence>()
            .AddSingleton<IMainProcessingModule, PbtMirrorMainProcessingModule>()
            .AddSingleton<IMigrationTelemetry>(NullMigrationTelemetry.Instance)
            .RegisterSingletonJsonRpcModule<IMigrationDebugRpcModule, MigrationDebugRpcModule>();

        // Registered unconditionally so `nethermind import-pbt` can always find it. Carrying [StepCommand] keeps it
        // out of a normal node start; it runs only when selected below or by name.
        builder
            .AddSingleton<PbtRebuilder>()
            .AddStep(typeof(ImportPbtFromPreimageFlat));

        if (config.ImportFromPreimageFlat)
        {
            builder.SelectStepTarget(typeof(ImportPbtFromPreimageFlat));
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
                            ctx.Resolve<IRefCountingMemoryProvider>(),
                            ctx.Resolve<IPbtConfig>(),
                            ctx.Resolve<IStateHeaderProvider>(),
                            ctx.Resolve<ILogManager>()));
    }
}
