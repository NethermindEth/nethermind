using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;

namespace Nethermind.Blockchain
{
    public class Mempool : IMempool
    {
        private readonly ConcurrentDictionary<Transaction, PeerGroups> _transactions =
            new ConcurrentDictionary<Transaction, PeerGroups>();

        private readonly ConcurrentDictionary<PublicKey, ISynchronizationPeer> _peers =
            new ConcurrentDictionary<PublicKey, ISynchronizationPeer>();

        private static readonly object obj = new object();
        private static readonly int _iterationsLimit = 2;
        private readonly ILogger _logger;
        private readonly Timer _timer;

        public Mempool(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _timer = new Timer {Interval = 1000};
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
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
            var peers = _peers.Select(p => p.Value).ToArray();
            var peerGroups = new PeerGroups();
            peerGroups.GenerateGroup(transaction, peers);
            _transactions.TryAdd(transaction, peerGroups);
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_transactions.Count == 0)
            {
                return;
            }

            var transactionsToRemove = new List<Transaction>();
            foreach ((Transaction transaction, PeerGroups peerGroups) in _transactions)
            {
                if (peerGroups.Groups.Count < _iterationsLimit)
                {
                    var peers = _peers.Select(p => p.Value).ToArray();
                    peerGroups.Notify(transaction, peers);
                    peerGroups.GenerateGroup(transaction, peers);
                    continue;
                }

                transactionsToRemove.Add(transaction);
            }

            for (var i = 0; i < transactionsToRemove.Count; i++)
            {
                _transactions.TryRemove(transactionsToRemove[i], out _);
            }
        }

        private class PeerGroups
        {
            public ConcurrentQueue<PeerGroup> Groups { get; } = new ConcurrentQueue<PeerGroup>();

            public void Notify(Transaction transaction, ISynchronizationPeer[] peers)
            {
                if (Groups.Count >= _iterationsLimit)
                {
                    return;
                }

                var existingPeers = Groups.SelectMany(p => p.Peers).ToArray();
                for (var i = 0; i < existingPeers.Length; i++)
                {
                    var peer = existingPeers[i];
                    peer.SendNewTransaction(transaction);
                }
            }

            public void GenerateGroup(Transaction transaction, ISynchronizationPeer[] peers)
            {
                //TODO: Get rid of lock
                lock (obj)
                {
                    if (Groups.Count >= _iterationsLimit)
                    {
                        return;
                    }

                    var availablePeers = GetAvailablePeers(transaction, peers);
                    if (availablePeers.Length == 0)
                    {
                        return;
                    }

                    var selectedPeers = SelectPeers(availablePeers, peers.Length, Groups.Count);
                    if (selectedPeers.Length == 0)
                    {
                        return;
                    }

                    var peerGroup = new PeerGroup(new ConcurrentBag<ISynchronizationPeer>(selectedPeers));
                    Groups.Enqueue(peerGroup);
                }
            }

            private ISynchronizationPeer[] SelectPeers(ISynchronizationPeer[] peers, int allPeersCount, int iteration)
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