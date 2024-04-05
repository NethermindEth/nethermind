// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Optimism;

public class OptimismTransactionsExecutorFactory : IBlockTransactionsExecutorFactory
{
    private readonly ISpecProvider _specProvider;
    private readonly ILogManager _logManager;

    public OptimismTransactionsExecutorFactory(ISpecProvider specProvider, ILogManager logManager)
    {
        _specProvider = specProvider;
        _logManager = logManager;
    }

    public IBlockProcessor.IBlockTransactionsExecutor Create(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv)
    {
        return new BlockProcessor.BlockProductionTransactionsExecutor(readOnlyTxProcessingEnv.TransactionProcessor,
            readOnlyTxProcessingEnv.StateProvider, new OptimismBlockProductionTransactionPicker(_specProvider),
            _logManager);
    }
}
