using Nethermind.Core;

namespace Nethermind.Blockchain.TransactionPools.Filters
{
    public class AcceptAnyTransactionPoolFilter : ITransactionPoolFilter
    {
        public bool CanAdd(Transaction transaction) => true;
        public bool CanDelete(Transaction transaction) => true;
    }
}