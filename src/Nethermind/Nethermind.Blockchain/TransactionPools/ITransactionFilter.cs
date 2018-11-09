using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionFilter
    {
        bool CanAdd(Transaction transaction);
        bool CanDelete(Transaction transaction);
    }
}