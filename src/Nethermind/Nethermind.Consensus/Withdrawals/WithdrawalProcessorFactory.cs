// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Withdrawals;

public class WithdrawalProcessorFactory(ILogManager logManager) : IWithdrawalProcessorFactory
{
    public IWithdrawalProcessor Create(IWorldState worldState, ITransactionProcessor transactionProcessor)
    {
        return new WithdrawalProcessor(worldState, logManager);
    }
}
