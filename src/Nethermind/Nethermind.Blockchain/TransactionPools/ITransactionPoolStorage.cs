using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools
{
    public interface ITransactionPoolStorage
    {
        Transaction Get(Keccak hash);
        Transaction[] GetAll();
        void Add(Transaction transaction);
        void Delete(Transaction transaction);
    }
}