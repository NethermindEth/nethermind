using System.Collections.Concurrent;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class InMemoryTransactionPoolStorage : ITransactionPoolStorage
    {
        private readonly ConcurrentDictionary<Keccak, Transaction> _transactions =
            new ConcurrentDictionary<Keccak, Transaction>();

        public Transaction Get(Keccak hash)
        {
            _transactions.TryGetValue(hash, out var transaction);

            return transaction;
        }

        public Transaction[] GetAll() => _transactions.Values.ToArray();

        public void Add(Transaction transaction) => _transactions.TryAdd(transaction.Hash, transaction);

        public void Delete(Transaction transaction) => _transactions.TryRemove(transaction.Hash, out _);
    }
}