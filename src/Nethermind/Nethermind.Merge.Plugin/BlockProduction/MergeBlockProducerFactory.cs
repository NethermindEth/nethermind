// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Producers;

namespace Nethermind.Merge.Plugin.BlockProduction;

/// <summary>
/// Decorates the underlying consensus <see cref="IBlockProducerFactory"/> with post-merge block production,
/// returning a <see cref="MergeBlockProducer"/> that switches between the pre-merge and post-merge producers.
/// </summary>
public sealed class MergeBlockProducerFactory(
    IBlockProducerFactory baseBlockProducerFactory,
    IBlockProducerEnvFactory blockProducerEnvFactory,
    PostMergeBlockProducerFactory postMergeBlockProducerFactory,
    IPoSSwitcher poSSwitcher,
    IBlockProductionPolicy blockProductionPolicy)
    : IBlockProducerFactory
{
    public IBlockProducer InitBlockProducer()
    {
        IMergeBlockProductionPolicy? mergeBlockProductionPolicy = blockProductionPolicy as IMergeBlockProductionPolicy;
        IBlockProducer? blockProducer = (mergeBlockProductionPolicy?.ShouldInitPreMergeBlockProduction() != false)
            ? baseBlockProducerFactory.InitBlockProducer()
            : null;

        IBlockProducerEnv blockProducerEnv = blockProducerEnvFactory.CreatePersistent();

        PostMergeBlockProducer postMergeBlockProducer = postMergeBlockProducerFactory.Create(blockProducerEnv);
        return new MergeBlockProducer(blockProducer, postMergeBlockProducer, poSSwitcher);
    }
}
