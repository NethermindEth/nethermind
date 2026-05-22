// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Installs <see cref="WitnessCapturingWorldStateProxy"/> as the main-processing
/// <see cref="IWorldState"/> decorator when <paramref name="enabled"/>; no-op otherwise.
/// </summary>
public sealed class WitnessCapturingMainProcessingModule(bool enabled) : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder)
    {
        if (enabled)
            builder.AddDecorator<IWorldState, WitnessCapturingWorldStateProxy>();
    }
}
