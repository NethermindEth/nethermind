// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;

namespace Nethermind.Consensus.Producers;

public static class IBlockProducerEnvFactoryExtensions
{
    public static IBlockProducerEnvFactory WithTransactionExecutorFactory(this IBlockProducerEnvFactory current,
        IBlockTransactionsExecutorFactory txFactory)
    {
        return new TransactionExecutorFactoryReplacingBlockProducerEnvFactory(current, txFactory);
    }

    private class TransactionExecutorFactoryReplacingBlockProducerEnvFactory(
        IBlockProducerEnvFactory baseFactory,
        IBlockTransactionsExecutorFactory txFactory) : IBlockProducerEnvFactory
    {
        public IBlockTransactionsExecutorFactory TransactionsExecutorFactory => txFactory;

        public BlockProducerEnv Create(ITxSource? additionalTxSource = null)
        {
            return baseFactory.Create(additionalTxSource);
        }
    }
}
