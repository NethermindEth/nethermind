
// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Shutter
{
    public class ShutterBlockProducerEnvFactory(
        IBlockProducerEnvFactory baseBlockProducerEnvFactory,
        ShutterTxSource txSource,
        ILogger logger) : IBlockProducerEnvFactory
    {
        public IBlockTransactionsExecutorFactory TransactionsExecutorFactory
        {
            get => baseBlockProducerEnvFactory.TransactionsExecutorFactory;
            set => baseBlockProducerEnvFactory.TransactionsExecutorFactory = value;
        }

        public BlockProducerEnv Create(ITxSource? additionalTxSource = null)
        {
            logger.Info("Creating Shutter block producer env");
            return baseBlockProducerEnvFactory.Create(txSource.Then(additionalTxSource));
        }
    }
}
