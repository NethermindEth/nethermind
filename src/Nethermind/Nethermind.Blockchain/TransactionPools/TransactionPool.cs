using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools
{
    public class TransactionPool : ITransactionPool
    {
        private static int _seed = Environment.TickCount;

        private static readonly ThreadLocal<Random> Random =
            new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref _seed)));

        private readonly ConcurrentDictionary<Keccak, Transaction> _pendingTransactions =
            new ConcurrentDictionary<Keccak, Transaction>();

        private readonly ConcurrentDictionary<Type, ITransactionFilter> _filters =
            new ConcurrentDictionary<Type, ITransactionFilter>();

        private readonly ITransactionStorage _transactionStorage;
        private readonly IReceiptStorage _receiptStorage;

        private readonly ConcurrentDictionary<PublicKey, ISynchronizationPeer> _peers =
            new ConcurrentDictionary<PublicKey, ISynchronizationPeer>();

        private readonly IEthereumSigner _signer;
        private readonly ILogger _logger;
        private readonly int _peerThreshold;

        public TransactionPool(ITransactionStorage transactionStorage, IReceiptStorage receiptStorage,
            IEthereumSigner signer, ILogManager logManager, int peerThreshold = 20)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _transactionStorage = transactionStorage;
            _receiptStorage = receiptStorage;
            _signer = signer;
            _peerThreshold = peerThreshold;
        }

        public Transaction[] GetPendingTransactions() => _pendingTransactions.Values.ToArray();
        public TransactionReceipt GetReceipt(Keccak hash) => _receiptStorage.Get(hash);

        public void AddFilter<T>(T filter) where T : ITransactionFilter
            => _filters.TryAdd(filter.GetType(), filter);

        public void AddPeer(ISynchronizationPeer peer)
        {
            if (!_peers.TryAdd(peer.NodeId.PublicKey, peer))
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Added a peer: {peer.ClientId}");
        }

        public void DeletePeer(NodeId nodeId)
        {
            if (!_peers.TryRemove(nodeId.PublicKey, out _))
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Removed a peer: {nodeId}");
        }

        public void AddTransaction(Transaction transaction, UInt256 blockNumber)
        {
            if (!_pendingTransactions.TryAdd(transaction.Hash, transaction))
            {
                return;
            }

            var recoveredAddress = _signer.RecoverAddress(transaction, blockNumber);
            if (recoveredAddress != transaction.SenderAddress)
            {
                throw new InvalidOperationException("Invalid signature");
            }

            NewPending?.Invoke(this, new TransactionEventArgs(transaction));
            NotifyPeers(SelectPeers(transaction), transaction);
            FilterAndStoreTransaction(transaction, blockNumber);
        }

        private void FilterAndStoreTransaction(Transaction transaction, UInt256 blockNumber)
        {
            var filters = _filters.Values;
            if (filters.Any(filter => !filter.IsValid(transaction)))
            {
                return;
            }

            _transactionStorage.Add(transaction, blockNumber);
            if (_logger.IsDebug) _logger.Debug($"Added a transaction: {transaction.Hash}");
        }

        public void RemoveTransaction(Keccak hash)
        {
            _pendingTransactions.TryRemove(hash, out var transaction);
            _transactionStorage.Delete(hash);
            if (_logger.IsDebug) _logger.Debug($"Deleted a transaction: {hash}");
        }

        public void AddReceipt(TransactionReceipt receipt)
        {
            _receiptStorage.Add(receipt);
        }

        public event EventHandler<TransactionEventArgs> NewPending;

        private void NotifyPeers(List<ISynchronizationPeer> peers, Transaction transaction)
        {
            if (peers.Count == 0)
            {
                return;
            }

            for (var i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                peer.SendNewTransaction(transaction);
            }

            if (_logger.IsDebug) _logger.Debug($"Notified {peers.Count} peers about a transaction: {transaction.Hash}");
        }

        private List<ISynchronizationPeer> SelectPeers(Transaction transaction)
        {
            var selectedPeers = new List<ISynchronizationPeer>();
            foreach (var peer in _peers.Values)
            {
                if (transaction.DeliveredBy.Equals(peer.NodeId.PublicKey))
                {
                    continue;
                }

                if (_peerThreshold < Random.Value.Next(1, 100))
                {
                    continue;
                }

                selectedPeers.Add(peer);
            }

            return selectedPeers;
        }
    }
}