using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;

namespace Nethermind.Blockchain
{
    public class Mempool : IMempool
    {
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<Transaction, PeerGroups> _transactions =
            new ConcurrentDictionary<Transaction, PeerGroups>();

        private readonly ConcurrentDictionary<PublicKey, ISynchronizationPeer> _peers =
            new ConcurrentDictionary<PublicKey, ISynchronizationPeer>();

        private readonly int _transactionsLimit;
        private readonly int _iterationsLimit;
        private readonly int _iterationInterval;
        private readonly Timer _timer;

        public Mempool(ILogManager logManager, int transactionsLimit = 4096, int iterationsLimit = 10,
            int iterationInterval = 1000)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _transactionsLimit = transactionsLimit;
            _iterationsLimit = iterationsLimit;
            _iterationInterval = iterationInterval;
            _timer = new Timer(OnTimerCallback, null, iterationInterval, Timeout.Infinite);
        }

        public void AddPeer(ISynchronizationPeer peer)
        {
            _peers.TryAdd(peer.NodeId.PublicKey, peer);
        }

        public void RemovePeer(ISynchronizationPeer peer)
        {
            _peers.TryRemove(peer.NodeId.PublicKey, out _);
        }

        public void AddTransaction(Transaction transaction)
        {
            if (_transactions.Count >= _transactionsLimit)
            {
                return;
            }

            var peers = _peers.Select(p => p.Value).ToArray();
            var peerGroups = new PeerGroups();
            peerGroups.GenerateGroup(transaction, peers, _iterationsLimit);
            _transactions.TryAdd(transaction, peerGroups);
        }

        private void OnTimerCallback(object state)
        {
            if (_transactions.Count == 0)
            {
                _timer.Change(1, Timeout.Infinite);

                return;
            }

            var transactionsToRemove = new List<Transaction>();
            foreach ((Transaction transaction, PeerGroups peerGroups) in _transactions)
            {
                if (peerGroups.Iterations < _iterationsLimit)
                {
                    var peers = _peers.Select(p => p.Value).ToArray();
                    peerGroups.Notify(transaction);
                    peerGroups.GenerateGroup(transaction, peers, _iterationsLimit);
                    continue;
                }

                transactionsToRemove.Add(transaction);
            }

            for (var i = 0; i < transactionsToRemove.Count; i++)
            {
                _transactions.TryRemove(transactionsToRemove[i], out _);
            }

            _timer.Change(_iterationInterval, Timeout.Infinite);
        }

        private class PeerGroups
        {
            private int _iterations;
            public int Iterations => _iterations;
            public ConcurrentQueue<PeerGroup> Groups { get; } = new ConcurrentQueue<PeerGroup>();

            public void Notify(Transaction transaction)
            {
                if (Groups.Count == 0)
                {
                    return;
                }

                if (Groups.TryPeek(out var peerGroup))
                {
                    var peers = peerGroup.Peers.ToArray();
                    for (var i = 0; i < peers.Length; i++)
                    {
                        var peer = peers[i];
                        peer.SendNewTransaction(transaction);
                    }
                }
            }

            public void GenerateGroup(Transaction transaction, ISynchronizationPeer[] peers, int iterationsLimit)
            {
                if (_iterations == iterationsLimit)
                {
                    return;
                }

                _iterations++;
                var availablePeers = GetAvailablePeers(transaction, peers);
                if (availablePeers.Length == 0)
                {
                    return;
                }

                var selectedPeers = SelectPeers(availablePeers, peers.Length);
                if (selectedPeers.Length == 0)
                {
                    return;
                }

                var peerGroup = new PeerGroup(new ConcurrentBag<ISynchronizationPeer>(selectedPeers));
                Groups.Enqueue(peerGroup);
            }

            private ISynchronizationPeer[] SelectPeers(ISynchronizationPeer[] peers, int allPeersCount)
            {
                //TODO: Select peers using a proper function.
                return peers.Take(2).ToArray();
            }

            private ISynchronizationPeer[] GetAvailablePeers(Transaction transaction, ISynchronizationPeer[] peers)
            {
                var availablePeers = new List<ISynchronizationPeer>();
                var addedPeers = Groups.SelectMany(p => p.Peers).ToArray();

                for (var i = 0; i < peers.Length; i++)
                {
                    var peer = peers[i];
                    var addPeer = !transaction.DeliveredBy.Equals(peer.NodeId.PublicKey);
                    if (!addPeer)
                    {
                        continue;
                    }

                    for (var j = 0; j < addedPeers.Length; j++)
                    {
                        var addedPeer = addedPeers[j];
                        if (peer.NodeId.PublicKey.Equals(addedPeer.NodeId.PublicKey))
                        {
                            addPeer = false;
                            break;
                        }
                    }

                    if (addPeer)
                    {
                        availablePeers.Add(peer);
                    }
                }

                return availablePeers.ToArray();
            }
        }

        private class PeerGroup
        {
            public ConcurrentBag<ISynchronizationPeer> Peers { get; }

            public PeerGroup(ConcurrentBag<ISynchronizationPeer> peers)
            {
                Peers = peers;
            }
        }
    }
}