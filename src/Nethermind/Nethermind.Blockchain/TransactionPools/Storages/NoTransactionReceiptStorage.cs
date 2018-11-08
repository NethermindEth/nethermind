using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class NoTransactionReceiptStorage : ITransactionReceiptStorage
    {
        public TransactionReceipt Get(Keccak hash) => null;

        public void Add(TransactionReceipt receipt)
        {
        }
    }
}