// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Executor
{
    public class AABlockProducerTransactionsExecutorFactory : IBlockTransactionsExecutorFactory
    {
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly ISigner _signer;
        private readonly Address[] _entryPointAddresses;

        public AABlockProducerTransactionsExecutorFactory(ISpecProvider specProvider, ILogManager logManager, ISigner signer, Address[] entryPointAddresses)
        {
            _specProvider = specProvider;
            _logManager = logManager;
            _signer = signer;
            _entryPointAddresses = entryPointAddresses;
        }

        public IBlockProcessor.IBlockTransactionsExecutor Create(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv)
            => new AABlockProducerTransactionsExecutor(
                readOnlyTxProcessingEnv.TransactionProcessor,
                readOnlyTxProcessingEnv.StateProvider,
                _specProvider,
                _logManager,
                _signer,
                _entryPointAddresses);
    }
}
