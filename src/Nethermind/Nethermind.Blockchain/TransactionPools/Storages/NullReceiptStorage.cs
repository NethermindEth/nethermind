using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class NullReceiptStorage : IReceiptStorage
    {
        public TransactionReceipt Get(Keccak hash) => null;

        public void Add(TransactionReceipt receipt)
        {
        }
    }
}