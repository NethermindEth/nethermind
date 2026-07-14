// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa.InitializationSteps;

namespace Nethermind.Consensus.AuRa;

internal sealed class AuRaBlockProducerFactory(StartBlockProducerAuRa blockProducerStarter, IBlockTree blockTree)
    : IBlockProducerFactory, IBlockProducerRunnerFactory
{
    public IBlockProducer InitBlockProducer() => blockProducerStarter.BuildProducer();

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer) =>
        new StandardBlockProducerRunner(blockProducerStarter.CreateTrigger(), blockTree, blockProducer);
}
