// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;

namespace Nethermind.Merge.Plugin.Synchronization;

/// <summary>Installs BAL reconstruction only in the main processing environment.</summary>
public sealed class FinalizedBlockAccessListModule(ISyncConfig syncConfig) : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder)
    {
        if (syncConfig.ReconstructFinalizedStateFromBlockAccessLists)
            builder.AddDecorator<IBlockProcessor, FinalizedBlockAccessListProcessor>();
    }
}
