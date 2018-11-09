using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.TransactionPools
{
    public class TransactionPool : ITransactionPool
    {
        private static readonly Random GlobalRandom = new Random();
        [ThreadStatic] private static Random _random;

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

        public Transaction[] PendingTransactions => _pendingTransactions.Values.ToArray();
        public TransactionReceipt GetReceipt(Keccak hash) => _receiptStorage.Get(hash);

        public void AddFilter<T>(T filter) where T : ITransactionFilter
            => _filters.TryAdd(typeof(T), filter);

        public void DeleteFilter<T>() where T : ITransactionFilter
            => _filters.TryRemove(typeof(T), out _);

        public void AddPeer(ISynchronizationPeer peer)
        {
            if (!_peers.TryAdd(peer.NodeId.PublicKey, peer))
            {
                return;
            }

            if (_logger.IsDebug)
            {
                _logger.Debug($"Added peer: {peer.ClientId}");
            }
        }

        public void DeletePeer(NodeId nodeId)
        {
            if (!_peers.TryRemove(nodeId.PublicKey, out _))
            {
                return;
            }

            if (_logger.IsDebug)
            {
                _logger.Debug($"Removed peer: {nodeId}");
            }
        }

        public void AddTransaction(Transaction transaction, UInt256 blockNumber)
        {
            if (_pendingTransactions.ContainsKey(transaction.Hash))
            {
                return;
            }

            var recoveredAddress = _signer.RecoverAddress(transaction, blockNumber);
            if (recoveredAddress != transaction.SenderAddress)
            {
                throw new InvalidOperationException("Invalid signature");
            }

            _pendingTransactions.TryAdd(transaction.Hash, transaction);
            NewPending?.Invoke(this, new TransactionEventArgs(transaction));
            NotifyPeers(SelectPeers(GetAvailablePeers(transaction)), transaction);
            FilterAndStoreTransaction(transaction, blockNumber);
        }

        private void FilterAndStoreTransaction(Transaction transaction, UInt256 blockNumber)
        {
            var filters = _filters.Values.ToArray();
            for (var i = 0; i < filters.Length; i++)
            {
                var filter = filters[i];
                if (!filter.CanAdd(transaction))
                {
                    return;
                }
            }

            _transactionStorage.Add(transaction, blockNumber);
            if (_logger.IsDebug)
            {
                _logger.Debug($"Added transaction: {transaction.Hash}");
            }
        }

        public void DeleteTransaction(Keccak hash)
        {
            _pendingTransactions.TryRemove(hash, out var transaction);
            var filters = _filters.Values.ToArray();
            for (var i = 0; i < filters.Length; i++)
            {
                var filter = filters[i];
                if (!filter.CanDelete(transaction))
                {
                    return;
                }
            }

            _transactionStorage.Delete(hash);
            if (_logger.IsDebug)
            {
                _logger.Debug($"Deleted transaction: {hash}");
            }
        }

        public void AddReceipt(TransactionReceipt receipt)
        {
            _receiptStorage.Add(receipt);
        }

        public event EventHandler<TransactionEventArgs> NewPending;

        private void NotifyPeers(ISynchronizationPeer[] peers, Transaction transaction)
        {
            if (peers.Length == 0)
            {
                return;
            }

            for (var i = 0; i < peers.Length; i++)
            {
                var peer = peers[i];
                peer.SendNewTransaction(transaction);
            }

            if (_logger.IsDebug)
            {
                _logger.Debug($"Notified {peers.Length} about transaction: {transaction.Hash}");
            }
        }

        private ISynchronizationPeer[] SelectPeers(ISynchronizationPeer[] availablePeers)
        {
            if (availablePeers.Length == 0)
            {
                return new ISynchronizationPeer[0];
            }

            var selectedPeers = new List<ISynchronizationPeer>();
            for (var i = 0; i < availablePeers.Length; i++)
            {
                var peer = availablePeers[i];
                if (_peerThreshold >= GetRandomNumber())
                {
                    selectedPeers.Add(peer);
                }
            }

            return selectedPeers.ToArray();
        }

        private ISynchronizationPeer[] GetAvailablePeers(Transaction transaction)
        {
            var peers = _peers.Values.ToArray();
            var availablePeers = new List<ISynchronizationPeer>();

            for (var i = 0; i < peers.Length; i++)
            {
                var peer = peers[i];
                if (transaction.DeliveredBy.Equals(peer.NodeId.PublicKey))
                {
                    continue;
                }

                availablePeers.Add(peer);
            }

            return availablePeers.ToArray();
        }

        private static int GetRandomNumber()
        {
            var instance = _random;
            if (instance == null)
            {
                int seed;
                lock (GlobalRandom)
                {
                    seed = GlobalRandom.Next();
                }

                _random = instance = new Random(seed);
            }

            return instance.Next(1, 100);
        }
    }
}