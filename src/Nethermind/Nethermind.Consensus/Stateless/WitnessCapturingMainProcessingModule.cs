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
public sealed class WitnessCapturingMainProcessingModule : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder) =>
        builder.AddDecorator<IWorldState, WitnessCapturingWorldStateProxy>();
}
