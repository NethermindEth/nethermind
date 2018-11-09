using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class NullTransactionStorage : ITransactionStorage
    {
        public Transaction Get(Keccak hash) => null;

        public Transaction[] GetAll() => new Transaction[0];

        public void Add(Transaction transaction, UInt256 blockNumber)
        {
        }

        public void Delete(Keccak hash)
        {
        }
    }
}