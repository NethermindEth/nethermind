// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;

namespace Nethermind.Merge.Plugin.BlockProduction;

/// <summary>
/// Decorates the underlying consensus <see cref="IBlockProducerRunnerFactory"/> with a
/// <see cref="MergeBlockProducerRunner"/> that runs the pre-merge and post-merge producers.
/// </summary>
public sealed class MergeBlockProducerRunnerFactory(
    IBlockProducerRunnerFactory baseRunnerFactory,
    IPoSSwitcher poSSwitcher,
    IManualBlockProductionTrigger manualBlockProductionTrigger,
    IBlockTree blockTree)
    : IBlockProducerRunnerFactory
{
    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        IMergeBlockProducer mergeBlockProducer = blockProducer as IMergeBlockProducer
            ?? throw new ArgumentException("Merge enabled, but block producer is not IMergeBlockProducer");

        IBlockProducer? preMergeBlockProducer = mergeBlockProducer.PreMergeBlockProducer;
        IBlockProducerRunner? preMergeRunner = preMergeBlockProducer is not null
            ? baseRunnerFactory.InitBlockProducerRunner(preMergeBlockProducer)
            : null;

        StandardBlockProducerRunner postMergeRunner = new(manualBlockProductionTrigger, blockTree, mergeBlockProducer);

        return new MergeBlockProducerRunner(preMergeRunner, postMergeRunner, poSSwitcher);
    }
}
