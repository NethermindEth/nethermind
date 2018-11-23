/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Timers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Timer = System.Timers.Timer;

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
        private readonly IPendingTransactionThresholdValidator _pendingTransactionThresholdValidator;
        private readonly ITimestamp _timestamp;

        private readonly ConcurrentDictionary<PublicKey, ISynchronizationPeer> _peers =
            new ConcurrentDictionary<PublicKey, ISynchronizationPeer>();

        private readonly IEthereumSigner _signer;
        private readonly ILogger _logger;

        private readonly int _peerNotificationThreshold;

        public TransactionPool(ITransactionStorage transactionStorage,
            IPendingTransactionThresholdValidator pendingTransactionThresholdValidator,
            ITimestamp timestamp, IEthereumSigner signer, ILogManager logManager,
            int removePendingTransactionInterval = 600,
            int peerNotificationThreshold = 20)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _transactionStorage = transactionStorage;
            _pendingTransactionThresholdValidator = pendingTransactionThresholdValidator;
            _timestamp = timestamp;
            _signer = signer;
            _peerNotificationThreshold = peerNotificationThreshold;
            if (removePendingTransactionInterval <= 0)
            {
                return;
            }

            var timer = new Timer {Interval = removePendingTransactionInterval * 1000};
            timer.Elapsed += OnTimerElapsed;
            timer.Start();
        }

        public Transaction[] GetPendingTransactions() => _pendingTransactions.Values.ToArray();

        public void AddFilter<T>(T filter) where T : ITransactionFilter
            => _filters.TryAdd(filter.GetType(), filter);

        public void AddPeer(ISynchronizationPeer peer)
        {
            if (!_peers.TryAdd(peer.NodeId.PublicKey, peer))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Added a peer: {peer.ClientId}");
        }

        public void RemovePeer(NodeId nodeId)
        {
            if (!_peers.TryRemove(nodeId.PublicKey, out _))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Removed a peer: {nodeId}");
        }

        public void AddTransaction(Transaction transaction, UInt256 blockNumber)
        {
            if (!_pendingTransactions.TryAdd(transaction.Hash, transaction))
            {
                return;
            }

            // TODO: we can use these to recover sender address much quicker when processing new blocks!
            transaction.SenderAddress = _signer.RecoverAddress(transaction, blockNumber);

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
            if (_logger.IsTrace) _logger.Trace($"Added a transaction: {transaction.Hash}");
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs eventArgs)
        {
            if (_pendingTransactions.Count == 0)
            {
                return;
            }

            var hashes = new List<Keccak>();
            var timestamp = new UInt256(_timestamp.EpochSeconds);
            foreach (var transaction in _pendingTransactions.Values)
            {
                if (_pendingTransactionThresholdValidator.IsRemovable(timestamp, transaction.Timestamp))
                {
                    hashes.Add(transaction.Hash);
                }
            }

            for (var i = 0; i < hashes.Count; i++)
            {
                if (_pendingTransactions.TryRemove(hashes[i], out var transaction))
                {
                    RemovedPending?.Invoke(this, new TransactionEventArgs(transaction));
                }
            }
        }

        public void RemoveTransaction(Keccak hash)
        {
            if (_pendingTransactions.TryRemove(hash, out var transaction))
            {
                RemovedPending?.Invoke(this, new TransactionEventArgs(transaction));
            }

            _transactionStorage.Delete(hash);
            if (_logger.IsTrace) _logger.Trace($"Deleted a transaction: {hash}");
        }

        public bool TryGetSender(Keccak hash, out Address sender)
        {
            bool found = _pendingTransactions.TryGetValue(hash, out Transaction transaction);
            sender = found ? transaction.SenderAddress : null;
            return found;
        }

        public event EventHandler<TransactionEventArgs> NewPending;
        public event EventHandler<TransactionEventArgs> RemovedPending;

        private void NotifyPeers(List<ISynchronizationPeer> peers, Transaction transaction)
        {
            if (peers.Count == 0)
            {
                return;
            }

            var timestamp = new UInt256(_timestamp.EpochSeconds);
            if (_pendingTransactionThresholdValidator.IsObsolete(timestamp, transaction.Timestamp))
            {
                return;
            }

            for (var i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                peer.SendNewTransaction(transaction);
            }

            if (_logger.IsTrace) _logger.Trace($"Notified {peers.Count} peers about a transaction: {transaction.Hash}");
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

                if (_peerNotificationThreshold < Random.Value.Next(1, 100))
                {
                    continue;
                }

                selectedPeers.Add(peer);
            }

            return selectedPeers;
        }
    }
}