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
        private readonly int _iterationsInterval;
        private readonly Timer _timer;

        public Mempool(ILogManager logManager, int transactionsLimit = 4096,
            int iterationsLimit = 10, int iterationsInterval = 1000)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _transactionsLimit = transactionsLimit;
            _iterationsLimit = iterationsLimit;
            _iterationsInterval = iterationsInterval;
            _timer = new Timer(OnTimerCallback, null, iterationsInterval, Timeout.Infinite);
        }

        public void AddPeer(ISynchronizationPeer peer)
        {
            if (_logger.IsDebug)
            {
                _logger.Debug($"Added peer: {peer.ClientId}");
            }

            _peers.TryAdd(peer.NodeId.PublicKey, peer);
        }

        public void RemovePeer(ISynchronizationPeer peer)
        {
            if (_logger.IsDebug)
            {
                _logger.Debug($"Removed peer: {peer.ClientId}");
            }

            _peers.TryRemove(peer.NodeId.PublicKey, out _);
            if (_transactions.Count == 0)
            {
                return;
            }

            foreach ((_, PeerGroups peerGroups) in _transactions)
            {
                foreach (var group in peerGroups.Groups)
                {
                    if (group.Peers.TryRemove(peer.NodeId.PublicKey, out _))
                    {
                        if (_logger.IsDebug)
                        {
                            _logger.Debug($"Removed peer: {peer.ClientId} from group.");
                        }
                    }
                }
            }
        }

        public void AddTransaction(Transaction transaction)
        {
            if (_transactions.Count >= _transactionsLimit)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"Transactions limit ({_transactionsLimit}) reached.");
                }

                return;
            }

            if (_logger.IsDebug)
            {
                _logger.Debug($"Added transaction: {transaction.Hash}");
            }

            var peers = _peers.Values.ToArray();
            var peerGroups = new PeerGroups();
            peerGroups.GenerateGroup(transaction, peers, _iterationsLimit);
            _transactions.TryAdd(transaction, peerGroups);
            if (_logger.IsDebug)
            {
                _logger.Debug($"Added new peer groups for transaction: {transaction.Hash}");
            }
        }

        private void OnTimerCallback(object state)
        {
            if (_transactions.Count == 0)
            {
                _timer.Change(_iterationsInterval, Timeout.Infinite);

                return;
            }

            if (_logger.IsDebug)
            {
                _logger.Debug($"Notifying peers about {_transactions.Count} transactions.");
            }

            var transactionsToRemove = new List<Transaction>();
            foreach ((Transaction transaction, PeerGroups peerGroups) in _transactions)
            {
                if (peerGroups.Iterations < _iterationsLimit)
                {
                    var peers = _peers.Values.ToArray();
                    peerGroups.Notify(transaction);
                    peerGroups.GenerateGroup(transaction, peers, _iterationsLimit);
                    continue;
                }

                transactionsToRemove.Add(transaction);
            }

            if (transactionsToRemove.Count == 0)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug($"No transactions found to be removed.");
                }

                _timer.Change(_iterationsInterval, Timeout.Infinite);

                return;
            }

            if (_logger.IsDebug)
            {
                _logger.Debug($"Removing: {_transactions.Count} transactions");
            }

            for (var i = 0; i < transactionsToRemove.Count; i++)
            {
                _transactions.TryRemove(transactionsToRemove[i], out _);
            }

            _timer.Change(_iterationsInterval, Timeout.Infinite);
        }

        private class PeerGroups
        {
            private static readonly Random Random = new Random();
            public int Iterations { get; private set; }
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
                        peer.Value.SendNewTransaction(transaction);
                    }
                }
            }

            public void GenerateGroup(Transaction transaction, ISynchronizationPeer[] peers, int iterationsLimit)
            {
                if (Iterations == iterationsLimit)
                {
                    return;
                }

                Iterations++;
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

                var peerGroup = new PeerGroup(selectedPeers.ToDictionary(p => p.NodeId.PublicKey, p => p));
                Groups.Enqueue(peerGroup);
            }

            private ISynchronizationPeer[] SelectPeers(ISynchronizationPeer[] peers, int allPeersCount)
            {
                if (peers.Length == 0 || allPeersCount == 0)
                {
                    return new ISynchronizationPeer[0];
                }

                var from = 1;
                var to = (int) Math.Floor(Math.Sqrt(peers.Length));
                if (Groups.TryPeek(out var peerGroup))
                {
                    from = peerGroup.Peers.Count;
                    if (from > to)
                    {
                        var temp = to;
                        to = from;
                        from = temp;
                    }
                }

                var take = Random.Next(from, to);

                return peers.Take(take).ToArray();
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
                        if (peer.NodeId.PublicKey.Equals(addedPeer.Value.NodeId.PublicKey))
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
            public ConcurrentDictionary<PublicKey, ISynchronizationPeer> Peers { get; }

            public PeerGroup(IDictionary<PublicKey, ISynchronizationPeer> peers)
            {
                Peers = new ConcurrentDictionary<PublicKey, ISynchronizationPeer>(peers);
            }
        }
    }
}