using System.Collections.Generic;
using Nethermind.Core;

namespace Ethereum.Test.Base.T8NUtils;

public class TransactionExecutionReport
{
    public List<RejectedTx> RejectedTransactionReceipts { get; set; } = [];
    public List<Transaction> ValidTransactions { get; set; } = [];
    public List<Transaction> SuccessfulTransactions { get; set; } = [];
    public List<TxReceipt> SuccessfulTransactionReceipts { get; set; } = [];
}
