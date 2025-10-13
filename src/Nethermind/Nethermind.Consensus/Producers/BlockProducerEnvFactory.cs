// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Producers
{
    public class BlockProducerEnvFactory(
        ILifetimeScope rootLifetime,
        IWorldStateManager worldStateManager,
        IBlockProducerTxSourceFactory txSourceFactory
    ) : IBlockProducerEnvFactory
    {
        protected virtual ContainerBuilder ConfigureBuilder(ContainerBuilder builder) => builder
            .AddScoped<ITxSource>(txSourceFactory.Create())
            .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped(BlockchainProcessor.Options.NoReceipts)
            .AddScoped<ITransactionProcessorAdapter, BuildUpTransactionProcessorAdapter>()
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
            .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()

            .AddScoped<IBlockProducerEnv, BlockProducerEnv>();

        public IBlockProducerEnv Create()
        {
            IWorldState worldState = new TracedAccessWorldState(worldStateManager.CreateResettableWorldState());
            ILifetimeScope lifetimeScope = rootLifetime.BeginLifetimeScope(builder =>
                ConfigureBuilder(builder)
                    .AddScoped(worldState));

            rootLifetime.Disposer.AddInstanceForAsyncDisposal(lifetimeScope);

            return lifetimeScope.Resolve<IBlockProducerEnv>();
        }
    }
}
