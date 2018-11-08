using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Filters
{
    public class AcceptAnyTransactionFilter : ITransactionFilter
    {
        public bool CanAdd(Transaction transaction) => true;
        public bool CanDelete(Keccak hash) => true;
    }
}