using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class NoTransactionStorage : ITransactionStorage
    {
        public Transaction Get(Keccak hash) => null;

        public Transaction[] GetAll() => new Transaction[0];

        public void Add(Transaction transaction)
        {
        }

        public void Delete(Keccak hash)
        {
        }
    }
}