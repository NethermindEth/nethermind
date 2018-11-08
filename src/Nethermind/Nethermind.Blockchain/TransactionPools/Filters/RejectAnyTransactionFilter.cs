using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Filters
{
    public class RejectAnyTransactionFilter : ITransactionFilter
    {
        public bool CanAdd(Transaction transaction) => false;
        public bool CanDelete(Keccak hash) => false;
    }
}