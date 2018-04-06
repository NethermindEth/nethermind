using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public class TransactionStore : ITransactionStore
    {
        private readonly Dictionary<Keccak, Transaction> _pending = new Dictionary<Keccak, Transaction>();
        private readonly Dictionary<Keccak, Transaction> _transactions = new Dictionary<Keccak, Transaction>();
        private readonly Dictionary<Keccak, TransactionReceipt> _transactionRecepits = new Dictionary<Keccak, TransactionReceipt>();
        private readonly HashSet<Keccak> _processedTransations = new HashSet<Keccak>();
        private readonly Dictionary<Keccak, Keccak> _blockHashes = new Dictionary<Keccak, Keccak>();

        public void AddTransaction(Transaction transaction)
        {
            Debug.Assert(transaction.Hash != null, "expecting only signed transactions here");
            _transactions[transaction.Hash] = transaction;
        }

        public void AddTransactionReceipt(Keccak transactionHash, TransactionReceipt transactionReceipt, Keccak blockHash)
        {
            _transactionRecepits[transactionHash] = transactionReceipt;
            _blockHashes[transactionHash] = blockHash;
            _processedTransations.Add(transactionHash);
        }

        public Transaction GetTransaction(Keccak transactionHash)
        {
            return _transactions.TryGetValue(transactionHash, out var transaction) ? transaction : null;
        }

        public TransactionReceipt GetTransactionReceipt(Keccak transactionHash)
        {
            return _transactionRecepits.TryGetValue(transactionHash, out var transaction) ? transaction : null;
        }

        public bool WasProcessed(Keccak transactionHash)
        {
            return _processedTransations.Contains(transactionHash);
        }

        public Keccak GetBlockHash(Keccak transactionHash)
        {
            return _blockHashes.TryGetValue(transactionHash, out var blockHash) ? blockHash : null;
        }

        public void AddPending(Transaction transaction)
        {
            _pending.Add(transaction.Hash, transaction);
        }

        public Transaction[] GetAllPending()
        {
            return _pending.Values.ToArray();
        }
    }
}