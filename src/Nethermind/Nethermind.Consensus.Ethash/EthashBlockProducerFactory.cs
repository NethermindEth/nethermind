// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;

namespace Nethermind.Consensus.Ethash;

internal sealed class EthashBlockProducerFactory(IManualBlockProductionTrigger manualBlockProductionTrigger, IBlockTree blockTree)
    : IBlockProducerFactory, IBlockProducerRunnerFactory
{
    public IBlockProducer? InitBlockProducer() => null;

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer) =>
        new StandardBlockProducerRunner(manualBlockProductionTrigger, blockTree, blockProducer);
}
