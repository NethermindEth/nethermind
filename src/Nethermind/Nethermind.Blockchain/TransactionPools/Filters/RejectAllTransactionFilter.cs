using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Filters
{
    public class RejectAllTransactionFilter : ITransactionFilter
    {
        public bool CanAdd(Transaction transaction) => false;
        public bool CanDelete(Transaction transaction) => false;
    }
}