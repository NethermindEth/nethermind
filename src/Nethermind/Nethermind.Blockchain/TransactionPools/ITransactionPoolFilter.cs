using Nethermind.Core;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionPoolFilter
    {
        bool CanAdd(Transaction transaction);
        bool CanDelete(Transaction transaction);
    }
}