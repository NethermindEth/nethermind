
// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Shutter
{
    public class ShutterBlockProducerEnvFactory(
        IBlockProducerEnvFactory baseBlockProducerEnvFactory,
        ShutterTxSource txSource) : IBlockProducerEnvFactory
    {
        public IBlockTransactionsExecutorFactory TransactionsExecutorFactory
        {
            get => baseBlockProducerEnvFactory.TransactionsExecutorFactory;
            set => baseBlockProducerEnvFactory.TransactionsExecutorFactory = value;
        }

        public BlockProducerEnv Create(ITxSource? additionalTxSource = null)
        {
            return baseBlockProducerEnvFactory.Create(txSource.Then(additionalTxSource));
        }
    }
}
