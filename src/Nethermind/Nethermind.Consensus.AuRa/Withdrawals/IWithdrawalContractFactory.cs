// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Withdrawals;

public interface IWithdrawalContractFactory
{
    IWithdrawalContract Create(ITransactionProcessor processor);
}
