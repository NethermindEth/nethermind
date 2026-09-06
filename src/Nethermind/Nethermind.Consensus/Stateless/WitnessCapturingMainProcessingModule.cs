// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// When enabled, installs the <see cref="WitnessCapturingBlockProcessor"/> selector on the main
/// processing pipeline. The selector delegates a witnessed block to the dedicated witness graph held by
/// <see cref="WitnessCapturingBlockProcessingEnv"/> (registered at the root scope by the merge plugin), leaving the
/// main world state, code repository and block-access-list manager untouched for every other block.
/// </summary>
public sealed class WitnessCapturingMainProcessingModule(bool enabled) : Module, IMainProcessingModule
{
    public WitnessCapturingMainProcessingModule(ISpecProvider specProvider)
        : this(specProvider.GetFinalSpec().IsEip7928Enabled)
    {
    }

    protected override void Load(ContainerBuilder builder)
    {
        if (!enabled) return;

        builder.AddDecorator<IBlockProcessor, WitnessCapturingBlockProcessor>();
    }
}
