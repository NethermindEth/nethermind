using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class NoTransactionPoolStorage : ITransactionPoolStorage
    {
        public Transaction Get(Keccak hash) => null;

        public Transaction[] GetAll() => new Transaction[0];

        public void Add(Transaction transaction)
        {
        }

        public void Delete(Transaction transaction)
        {
        }
    }
}