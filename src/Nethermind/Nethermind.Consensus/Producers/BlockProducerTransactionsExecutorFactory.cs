// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Producers
{
    // TODO: can we remove this?
    public class BlockProducerTransactionsExecutorFactory : IBlockTransactionsExecutorFactory
    {
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;

        public BlockProducerTransactionsExecutorFactory(ISpecProvider specProvider, ILogManager logManager)
        {
            _specProvider = specProvider;
            _logManager = logManager;
        }

        public IBlockProcessor.IBlockTransactionsExecutor Create(IReadOnlyTxProcessingScope readOnlyTxProcessingEnv) =>
            new BlockProcessor.BlockProductionTransactionsExecutor(readOnlyTxProcessingEnv, _specProvider, _logManager);
    }
}
