// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusWrapperPlugin : INethermindPlugin
    {
        IBlockProducer InitBlockProducer(IBlockProducerFactory baseBlockProducerFactory);

        /// <summary>
        /// Initializes the <see cref="IBlockProducerRunner"/>.
        /// </summary>
        /// <remarks>
        /// BE CAREFUL IF MORE THAN ONE <see cref="IConsensusWrapperPlugin"/> OVERRIDES THIS METHOD AT A TIME.
        /// SEE <see cref="InitBlockProducer"/> FOR MORE DETAILS ON THE INITIALIZATION PROCESS.
        /// </remarks>
        IBlockProducerRunner InitBlockProducerRunner(IBlockProducerRunnerFactory baseRunnerFactory,
            IBlockProducer blockProducer) => baseRunnerFactory.InitBlockProducerRunner(blockProducer);
    }
}
