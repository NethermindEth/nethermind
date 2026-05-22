// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// On EIP-7928 chains, installs <see cref="WitnessCapturingWorldStateProxy"/> as the main-processing
/// <see cref="IWorldState"/> decorator and as a typed singleton (so the JSON-RPC handler can take it
/// directly), and wraps <see cref="IBlockProcessor"/> with <see cref="WitnessCapturingBlockProcessor"/>
/// so each <c>ProcessOne</c> arms/drains the proxy.
/// </summary>
public sealed class WitnessCapturingMainProcessingModule(ISpecProvider specProvider) : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder)
    {
        if (!specProvider.GetFinalSpec().IsEip7928Enabled) return;

        builder.AddDecorator<IWorldState, WitnessCapturingWorldStateProxy>();
        // Expose the SAME instance as a typed singleton via cast through IWorldState.
        builder.AddSingleton<WitnessCapturingWorldStateProxy>(ctx =>
            (WitnessCapturingWorldStateProxy)ctx.Resolve<IWorldState>());
        builder.AddDecorator<IBlockProcessor, WitnessCapturingBlockProcessor>();
    }
}
