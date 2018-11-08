using System.Collections.Concurrent;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools.Storages
{
    public class InMemoryTransactionStorage : ITransactionStorage
    {
        private readonly ConcurrentDictionary<Keccak, Transaction> _transactions =
            new ConcurrentDictionary<Keccak, Transaction>();

        public Transaction Get(Keccak hash)
        {
            _transactions.TryGetValue(hash, out var transaction);

            return transaction;
        }

        public Transaction[] GetAll() => _transactions.Values.ToArray();

        public void Add(Transaction transaction, UInt256 blockNumber)
            => _transactions.TryAdd(transaction.Hash, transaction);

        public void Delete(Keccak hash) => _transactions.TryRemove(hash, out _);
    }
}