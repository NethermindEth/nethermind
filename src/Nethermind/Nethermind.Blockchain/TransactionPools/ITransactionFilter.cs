using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionFilter
    {
        bool IsValid(Transaction transaction);
    }
}