// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Installs BAL reconstruction only in the main processing environment of chains that adopt EIP-7928.</summary>
public sealed class FinalizedBlockAccessListModule(ISyncConfig syncConfig, ISpecProvider specProvider) : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder)
    {
        if (syncConfig.ReconstructFinalizedStateFromBlockAccessLists && specProvider.GetFinalSpec().IsEip7928Enabled)
            builder.AddDecorator<IBlockProcessor, FinalizedBlockAccessListProcessor>();
    }
}
