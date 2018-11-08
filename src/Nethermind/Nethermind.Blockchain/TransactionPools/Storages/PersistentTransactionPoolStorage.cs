using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class PersistentTransactionPoolStorage : ITransactionPoolStorage
    {
        public Transaction Get(Keccak hash)
        {
            throw new System.NotImplementedException();
        }

        public Transaction[] GetAll()
        {
            throw new System.NotImplementedException();
        }

        public void Add(Transaction transaction)
        {
            throw new System.NotImplementedException();
        }

        public void Delete(Transaction transaction)
        {
            throw new System.NotImplementedException();
        }
    }
}