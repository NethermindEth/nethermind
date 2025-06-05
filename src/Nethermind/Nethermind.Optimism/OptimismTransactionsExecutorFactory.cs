// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Optimism;

public class OptimismTransactionsExecutorFactory : IBlockTransactionsExecutorFactory
{
    private readonly ISpecProvider _specProvider;
    private readonly ILogManager _logManager;
    private readonly long _maxTxLengthKilobytes;

    public OptimismTransactionsExecutorFactory(ISpecProvider specProvider, long maxTxLengthKilobytes, ILogManager logManager)
    {
        _maxTxLengthKilobytes = maxTxLengthKilobytes;
        _specProvider = specProvider;
        _logManager = logManager;
    }

    public IBlockProcessor.IBlockTransactionsExecutor Create(IReadOnlyTxProcessingScope readOnlyTxProcessingEnv)
    {
        return new BlockProcessor.BlockProductionTransactionsExecutor(readOnlyTxProcessingEnv.TransactionProcessor,
            readOnlyTxProcessingEnv.WorldState, new OptimismBlockProductionTransactionPicker(_specProvider, _maxTxLengthKilobytes),
            _logManager);
    }
}
