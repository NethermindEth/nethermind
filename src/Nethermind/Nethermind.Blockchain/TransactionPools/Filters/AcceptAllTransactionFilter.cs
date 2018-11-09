using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Filters
{
    public class AcceptAllTransactionFilter : ITransactionFilter
    {
        public bool CanAdd(Transaction transaction) => true;
        public bool CanDelete(Transaction transaction) => true;
    }
}