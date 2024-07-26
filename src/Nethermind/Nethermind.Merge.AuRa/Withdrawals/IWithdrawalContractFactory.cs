// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;
using Nethermind.Merge.AuRa.Contracts;

namespace Nethermind.Merge.AuRa.Withdrawals;

public interface IWithdrawalContractFactory
{
    IWithdrawalContract Create(ITransactionProcessor processor);
}
