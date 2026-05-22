// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Installs <see cref="WitnessCapturingWorldStateProxy"/> as the main-processing
/// <see cref="IWorldState"/> decorator when EIP-7928 is enabled on the final spec.
/// </summary>
public sealed class WitnessCapturingMainProcessingModule(ISpecProvider specProvider) : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder)
    {
        if (specProvider.GetFinalSpec().IsEip7928Enabled)
            builder.AddDecorator<IWorldState, WitnessCapturingWorldStateProxy>();
    }
}
