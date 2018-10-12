using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain.Filters
{
    public interface IFilterManager
    {
        FilterLog[] GetLogs(int filterId);
        void AddTransactionReceipt(TransactionReceiptContext receiptContext);
    }
}