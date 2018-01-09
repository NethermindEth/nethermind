using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.Core.Crypto;

namespace Nevermind.Evm
{
    public class TransactionStore : ITransactionStore
    {
        private readonly Dictionary<Keccak, Transaction> _transactions = new Dictionary<Keccak, Transaction>();
        private readonly Dictionary<Keccak, TransactionReceipt> _transactionRecepits = new Dictionary<Keccak, TransactionReceipt>();
        private readonly HashSet<Keccak> _processedTransations = new HashSet<Keccak>();
        private readonly Dictionary<Keccak, Keccak> _blockHashes = new Dictionary<Keccak, Keccak>();

        public void AddTransaction(Transaction transaction)
        {
            _transactions[transaction.Hash] = transaction;
        }

        public void AddTransactionReceipt(Keccak transactionHash, TransactionReceipt transactionReceipt, Keccak blockhash)
        {
            _transactionRecepits[transactionHash] = transactionReceipt;
            _blockHashes[transactionHash] = blockhash;
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

        public Keccak? GetBlockHash(Keccak transactionHash)
        {
            return _blockHashes.TryGetValue(transactionHash, out var blockHash) ? blockHash : (Keccak?)null;
        }
    }
}