using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionStorage
    {
        Transaction Get(Keccak hash);
        Transaction[] GetAll();
        void Add(Transaction transaction, UInt256 blockNumber);
        void Delete(Keccak hash);
    }
}