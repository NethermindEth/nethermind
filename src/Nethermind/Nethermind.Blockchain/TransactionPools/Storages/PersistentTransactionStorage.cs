using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class PersistentTransactionStorage : ITransactionStorage
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

        public void Delete(Keccak hash)
        {
            throw new System.NotImplementedException();
        }
    }
}