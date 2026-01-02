// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Evm.T8n.JsonTypes;


public class TransactionExecutionReport
{
    public List<RejectedTx> RejectedTransactionReceipts { get; set; } = [];
    public List<Transaction> ValidTransactions { get; set; } = [];
    public List<Transaction> SuccessfulTransactions { get; set; } = [];
    public List<TxReceipt> SuccessfulTransactionReceipts { get; set; } = [];
}
