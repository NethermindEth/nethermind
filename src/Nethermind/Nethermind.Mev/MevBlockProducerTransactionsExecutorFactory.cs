// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Mev
{
    public class MevBlockProducerTransactionsExecutorFactory : IBlockTransactionsExecutorFactory
    {
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;

        public MevBlockProducerTransactionsExecutorFactory(ISpecProvider specProvider, ILogManager logManager)
        {
            _specProvider = specProvider;
            _logManager = logManager;
        }

        public IBlockProcessor.IBlockTransactionsExecutor Create(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv) =>
            new MevBlockProductionTransactionsExecutor(readOnlyTxProcessingEnv, _specProvider, _logManager);
    }
}
