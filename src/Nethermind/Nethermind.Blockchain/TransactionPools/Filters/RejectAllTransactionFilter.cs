using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Filters
{
    public class RejectAllTransactionFilter : ITransactionFilter
    {
        public bool IsValid(Transaction transaction) => false;
    }
}