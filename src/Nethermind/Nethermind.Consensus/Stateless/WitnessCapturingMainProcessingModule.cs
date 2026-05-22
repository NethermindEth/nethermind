// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.State;

namespace Nethermind.Merge.Plugin;

/// <summary>
/// Autofac <see cref="IMainProcessingModule"/> that installs
/// <see cref="WitnessCapturingWorldStateProxy"/> as a decorator over the main
/// processing scope's <see cref="IWorldState"/>.
/// </summary>
/// <remarks>
/// Gated on <paramref name="enabled"/>: pre-Amsterdam chains pay no per-call
/// proxy overhead because the decorator is not registered at all. The flag is
/// derived from <c>ISpecProvider.GetFinalSpec().IsEip7928Enabled</c> at
/// container build time.
/// </remarks>
public sealed class WitnessCapturingMainProcessingModule(bool enabled) : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder)
    {
        if (enabled)
            builder.AddDecorator<IWorldState, WitnessCapturingWorldStateProxy>();
    }
}
