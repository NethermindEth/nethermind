using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public interface IFilterManager
    {
        IReadOnlyCollection<FilterLog> GetLogs(int filterId);
        void AddTransactionReceipt(TransactionReceiptContext receiptContext);
    }
}