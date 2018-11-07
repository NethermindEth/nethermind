using Nethermind.Core;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionPoolStrategy
    {
        void AddTransaction(Transaction transaction);
        void UpdateTransaction(Transaction transaction);
    }
}