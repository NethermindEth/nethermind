// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Withdrawals;

/// <summary>
/// Creates an <see cref="IWithdrawalProcessor"/> for use with a specific <see cref="IWorldState"/>
/// and <see cref="ITransactionProcessor"/>. This allows chain-specific withdrawal processors
/// (e.g. AuRa, Optimism) to be constructed with the correct dependencies.
/// </summary>
public interface IWithdrawalProcessorFactory
{
    IWithdrawalProcessor Create(IWorldState worldState, ITransactionProcessor transactionProcessor);
}
