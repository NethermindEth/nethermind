// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Withdrawals;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Optimism;

public class OptimismWithdrawalProcessorFactory(
    IOptimismSpecHelper specHelper,
    ILogManager logManager) : IWithdrawalProcessorFactory
{
    public IWithdrawalProcessor Create(IWorldState worldState, ITransactionProcessor transactionProcessor) => new OptimismWithdrawalProcessor(worldState, logManager, specHelper);
}
