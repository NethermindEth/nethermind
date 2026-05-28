// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// On EIP-7928 chains, wires up in-flight witness capture for the main processing pipeline:
/// installs the thin <see cref="WitnessCapturingWorldStateProxy"/> as the <see cref="IWorldState"/>
/// decorator, registers the <see cref="WitnessRendezvous"/> for handler↔processor coordination,
/// and decorates <see cref="IBlockProcessor"/> with <see cref="WitnessCapturingBlockProcessor"/>.
/// </summary>
public sealed class WitnessCapturingMainProcessingModule(ISpecProvider specProvider) : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder)
    {
        if (!specProvider.GetFinalSpec().IsEip7928Enabled) return;

        builder.AddSingleton<WitnessCapturingTrieStore>(ctx =>
            new WitnessCapturingTrieStore(ctx.Resolve<IWorldStateManager>().CreateReadOnlyTrieStore()));

        builder.AddDecorator<IWorldState, WitnessCapturingWorldStateProxy>();
        // Expose the same proxy instance as a typed singleton so the block-processor decorator can
        // take it directly. Cast through IWorldState because Autofac doesn't model decorator chains
        // as typed singletons.
        builder.AddSingleton<WitnessCapturingWorldStateProxy>(ctx =>
            (WitnessCapturingWorldStateProxy)ctx.Resolve<IWorldState>());
        builder.AddDecorator<ICodeInfoRepository, WitnessCapturingCodeInfoRepository>();
        builder.AddDecorator<IBlockProcessor, WitnessCapturingBlockProcessor>();
    }
}
