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
        private static readonly Random Random = new Random();

        private readonly ConcurrentDictionary<PublicKey, ISynchronizationPeer> _peers =
            new ConcurrentDictionary<PublicKey, ISynchronizationPeer>();

        private readonly ITransactionPoolStrategy _strategy;
        private readonly ILogger _logger;
        private readonly int _peerThrehshold;

        public TransactionPool(ITransactionPoolStrategy strategy, ILogManager logManager,
            int peerThrehshold = 20)
        {
            _strategy = strategy;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _peerThrehshold = peerThrehshold;
        }

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

        public void AddTransaction(Transaction transaction)
        {
            if (_logger.IsDebug)
            {
                _logger.Debug($"Added transaction: {transaction.Hash}");
            }

            NotifyPeers(SelectPeers(GetAvailablePeers(transaction)), transaction);
            _strategy.AddTransaction(transaction);
        }

        public void UpdateTransaction(Transaction transaction)
        {
            _strategy.UpdateTransaction(transaction);
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
                if (_peerThrehshold >= Random.Next(1, 100))
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
    }
}