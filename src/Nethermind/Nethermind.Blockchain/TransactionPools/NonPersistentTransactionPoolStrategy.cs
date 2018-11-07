using Nethermind.Core;

namespace Nethermind.Blockchain.TransactionPools
{
    public class NonPersistentTransactionPoolStrategy : ITransactionPoolStrategy
    {
        public void AddTransaction(Transaction transaction)
        {
        }

        public void UpdateTransaction(Transaction transaction)
        {
        }
    }
}