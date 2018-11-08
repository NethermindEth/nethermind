using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;

namespace Nethermind.Blockchain.TransactionPools
{
    public class TransactionPool : ITransactionPool
    {
        private static readonly Random GlobalRandom = new Random();
        [ThreadStatic] private static Random _random;

        private readonly ConcurrentDictionary<Type, ITransactionPoolFilter> _filters =
            new ConcurrentDictionary<Type, ITransactionPoolFilter>();

        private readonly ConcurrentDictionary<Type, ITransactionPoolStorage> _storages =
            new ConcurrentDictionary<Type, ITransactionPoolStorage>();

        private readonly ConcurrentDictionary<PublicKey, ISynchronizationPeer> _peers =
            new ConcurrentDictionary<PublicKey, ISynchronizationPeer>();

        private readonly ILogger _logger;
        private readonly int _peerThrehshold;

        public TransactionPool(ILogManager logManager, int peerThrehshold = 20)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _peerThrehshold = peerThrehshold;
        }

        public void AddFilter<T>(T filter) where T : ITransactionPoolFilter
            => _filters.TryAdd(typeof(T), filter);

        public void DeleteFilter<T>() where T : ITransactionPoolFilter
            => _filters.TryRemove(typeof(T), out _);

        public void AddStorage<T>(T storage) where T : ITransactionPoolStorage
            => _storages.TryAdd(storage.GetType(), storage);

        public void DeleteStorage<T>() where T : ITransactionPoolStorage
            => _storages.TryRemove(typeof(T), out _);

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

        public void RemovePeer(ISynchronizationPeer peer)
        {
            if (!_peers.TryRemove(peer.NodeId.PublicKey, out _))
            {
                return;
            }

            if (_logger.IsDebug)
            {
                _logger.Debug($"Removed peer: {peer.ClientId}");
            }
        }

        public void TryAddTransaction(Transaction transaction)
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

            var storages = _storages.Values.ToArray();
            for (var i = 0; i < storages.Length; i++)
            {
                var storage = storages[i];
                storage.Add(transaction);
            }

            if (_logger.IsDebug)
            {
                _logger.Debug($"Added transaction: {transaction.Hash}");
            }

            NotifyPeers(SelectPeers(GetAvailablePeers(transaction)), transaction);
        }

        public void TryDeleteTransaction(Transaction transaction)
        {
            var filters = _filters.Values.ToArray();
            for (var i = 0; i < filters.Length; i++)
            {
                var filter = filters[i];
                if (!filter.CanDelete(transaction))
                {
                    return;
                }
            }

            var storages = _storages.Values.ToArray();
            for (var i = 0; i < storages.Length; i++)
            {
                var storage = storages[i];
                storage.Delete(transaction);
            }

            _logger.Debug($"Deleted transaction: {transaction.Hash}");
        }

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
                if (_peerThrehshold >= GetRandomNumber())
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
                lock (GlobalRandom) seed = GlobalRandom.Next();
                _random = instance = new Random(seed);
            }

            return instance.Next(1, 100);
        }
    }
}