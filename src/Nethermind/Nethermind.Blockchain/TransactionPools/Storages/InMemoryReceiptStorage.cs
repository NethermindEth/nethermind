using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class InMemoryReceiptStorage : IReceiptStorage
    {
        private readonly ConcurrentDictionary<Keccak, TransactionReceipt> _receipts =
            new ConcurrentDictionary<Keccak, TransactionReceipt>();

        public TransactionReceipt Get(Keccak hash)
        {
            _receipts.TryGetValue(hash, out var transaction);

            return transaction;
        }

        public void Add(TransactionReceipt receipt)
            => _receipts.TryAdd(receipt.TransactionHash, receipt);
    }
}