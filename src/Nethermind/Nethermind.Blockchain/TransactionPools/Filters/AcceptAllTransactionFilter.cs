using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Filters
{
    public class AcceptAllTransactionFilter : ITransactionFilter
    {
        public bool IsValid(Transaction transaction) => true;
    }
}