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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.Blockchain.TxPools
{
    /// <summary>
    /// Stores all pending transactions. These will be used by block producer if this node is a miner / validator
    /// or simply for broadcasting and tracing in other cases.
    /// </summary>
    public class TxPool : ITxPool, IDisposable
    {
        /// <summary>
        /// Notification threshold randomizer seed
        /// </summary>
        private static int _seed = Environment.TickCount;
        
        /// <summary>
        /// Random number generator for peer notification threshold - no need to be securely random.
        /// </summary>
        private static readonly ThreadLocal<Random> Random =
            new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref _seed)));
        
        private readonly ISpecProvider _specProvider;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ILogger _logger;

        /// <summary>
        /// All pending transactions.
        /// </summary>
        private readonly ConcurrentDictionary<Keccak, Transaction> _pendingTransactions =
            new ConcurrentDictionary<Keccak, Transaction>();

        /// <summary>
        /// Transactions that should never be removed. (TODO: we should always remove transactions that are incorrect due to nonce overrides)
        /// </summary>
        private readonly ConcurrentDictionary<Keccak, bool> _nonEvictableTransactions =
            new ConcurrentDictionary<Keccak, bool>();
        
        /// <summary>
        /// Transactions published locally (initiated by this node users).
        /// </summary>
        private ConcurrentDictionary<Keccak, Transaction> _ownTransactions
            = new ConcurrentDictionary<Keccak, Transaction>();

        /// <summary>
        /// Filters on pending transactions.
        /// </summary>
        private readonly ConcurrentDictionary<Type, ITxFilter> _filters =
            new ConcurrentDictionary<Type, ITxFilter>();

        private readonly ITimestamper _timestamper;
        
        /// <summary>
        /// Long term storage for pending transactions.
        /// </summary>
        private readonly ITxStorage _txStorage;
        
        /// <summary>
        /// Defines which of the pending transactions can be removed and should not be broadcast or included in blocks any more. 
        /// </summary>
        private readonly IPendingTxThresholdValidator _pendingTxThresholdValidator;

        /// <summary>
        /// Connected peers that can be notified about transactions.
        /// </summary>
        private readonly ConcurrentDictionary<PublicKey, ISyncPeer> _peers = new ConcurrentDictionary<PublicKey, ISyncPeer>();

        /// <summary>
        /// Timer for rebroadcasting pending own transactions.
        /// </summary>
        private readonly Timer _ownTimer;
        
        /// <summary>
        /// Timer for removing obsolete transactions.
        /// </summary>
        private Timer _transactionRemovalTimer;
        
        /// <summary>
        /// Defines the percentage of peers that will be notified about pending transactions on average.
        /// </summary>
        private readonly int _peerNotificationThreshold;

        public TxPool(
            ITxStorage txStorage,
            ITimestamper timestamper,
            IEthereumEcdsa ecdsa,
            ISpecProvider specProvider,
            ITxPoolConfig txPoolConfig,
            ILogManager logManager)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txStorage = txStorage ?? throw new ArgumentNullException(nameof(txStorage));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            
            _peerNotificationThreshold = txPoolConfig.PeerNotificationThreshold;
            
            _ownTimer = new Timer(500);
            _ownTimer.Elapsed += OwnTimerOnElapsed;
            _ownTimer.AutoReset = false;
            _ownTimer.Start();
            
            _pendingTxThresholdValidator = new PendingTxThresholdValidator(txPoolConfig);
            int removeIntervalInSeconds = txPoolConfig.RemovePendingTransactionInterval;
            if (removeIntervalInSeconds <= 0)
            {
                return;
            }

            _transactionRemovalTimer = new Timer(removeIntervalInSeconds * 1000);
            _transactionRemovalTimer.Elapsed += RemovalTimerElapsed;
            _transactionRemovalTimer.AutoReset = false;
            _transactionRemovalTimer.Start();
        }

        public Transaction[] GetPendingTransactions() => _pendingTransactions.Values.ToArray();

        public void AddFilter<T>(T filter) where T : ITxFilter
            => _filters.TryAdd(filter.GetType(), filter);

        public void AddPeer(ISyncPeer peer)
        {
            if (!_peers.TryAdd(peer.Node.Id, peer))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Added a peer: {peer.ClientId}");
        }

        public void RemovePeer(PublicKey nodeId)
        {
            if (!_peers.TryRemove(nodeId, out _))
            {
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Removed a peer: {nodeId}");
        }

        public AddTxResult AddTransaction(Transaction transaction, long blockNumber, bool doNotEvict = false)
        {
            Metrics.PendingTransactionsReceived++;
            if (doNotEvict)
            {
                _nonEvictableTransactions.TryAdd(transaction.Hash, true);
                if (_logger.IsDebug) _logger.Debug($"Added a transaction: {transaction.Hash} that will not be evicted.");
            }

            // note that we are discarding here the old signature scheme (without ChainId)
            if (transaction.Signature.GetChainId == null)
            {
                Metrics.PendingTransactionsDiscarded++;
                return AddTxResult.OldScheme;
            }

            if (transaction.Signature.GetChainId != _specProvider.ChainId)
            {
                Metrics.PendingTransactionsDiscarded++;
                return AddTxResult.InvalidChainId;
            }

            if (!_pendingTransactions.TryAdd(transaction.Hash, transaction))
            {
                Metrics.PendingTransactionsKnown++;
                return AddTxResult.AlreadyKnown;
            }

            if (_txStorage.Get(transaction.Hash) != null)
            {
                Metrics.PendingTransactionsKnown++;
                return AddTxResult.AlreadyKnown;
            }

            transaction.SenderAddress = _ecdsa.RecoverAddress(transaction, blockNumber);
            // check nonce

            if (transaction.DeliveredBy == null)
            {
                _ownTransactions.TryAdd(transaction.Hash, transaction);
                _ownTimer.Enabled = true;

                if (_logger.IsInfo) _logger.Info($"Broadcasting own transaction {transaction.Hash} to {_peers.Count} peers");
            }
            
            NotifySelectedPeers(transaction);

            FilterAndStoreTransaction(transaction, blockNumber);
            NewPending?.Invoke(this, new TxEventArgs(transaction));
            return AddTxResult.Added;
        }

        public void RemoveTransaction(Keccak hash)
        {
            if (_pendingTransactions.TryRemove(hash, out var transaction))
            {
                RemovedPending?.Invoke(this, new TxEventArgs(transaction));
                _nonEvictableTransactions.TryRemove(hash, out _);
            }

            if (_ownTransactions.Count != 0)
            {
                bool ownIncluded = _ownTransactions.TryRemove(hash, out _);
                if (ownIncluded)
                {
                    if (_logger.IsInfo) _logger.Trace($"Transaction {hash} created on this node was included in the block");
                }
            }

            _txStorage.Delete(hash);
            if (_logger.IsTrace) _logger.Trace($"Deleted a transaction: {hash}");
        }

        public bool TryGetSender(Keccak hash, out Address sender)
        {
            bool found = _pendingTransactions.TryGetValue(hash, out Transaction transaction);
            sender = found ? transaction.SenderAddress : null;
            return found;
        }

        public void Dispose()
        {
            _ownTimer?.Dispose();
            _transactionRemovalTimer?.Dispose();
        }
        
        public event EventHandler<TxEventArgs> NewPending;
        public event EventHandler<TxEventArgs> RemovedPending;
        
        private void Notify(ISyncPeer peer, Transaction transaction)
        {
            var timestamp = new UInt256(_timestamper.EpochSeconds);
            if (_pendingTxThresholdValidator.IsObsolete(timestamp, transaction.Timestamp))
            {
                return;
            }

            Metrics.PendingTransactionsSent++;
            peer.SendNewTransaction(transaction);

            if (_logger.IsTrace) _logger.Trace($"Notified {peer.Node.Id} about a transaction: {transaction.Hash}");
        }

        private void NotifyAllPeers(Transaction transaction)
        {
            foreach ((_, ISyncPeer peer) in _peers)
            {
                Notify(peer, transaction);
            }
        }
        
        private void NotifySelectedPeers(Transaction transaction)
        {
            foreach ((_, ISyncPeer peer) in _peers)
            {
                if (transaction.DeliveredBy == null)
                {
                    Notify(peer, transaction);
                    continue;
                }
                
                if (transaction.DeliveredBy.Equals(peer.Node.Id))
                {
                    continue;
                }

                if (_peerNotificationThreshold < Random.Value.Next(1, 100))
                {
                    continue;
                }

                Notify(peer, transaction);
            }
        }
        
                private void FilterAndStoreTransaction(Transaction transaction, long blockNumber)
        {
            var filters = _filters.Values;
            if (filters.Any(filter => !filter.IsValid(transaction)))
            {
                return;
            }

            _txStorage.Add(transaction, blockNumber);
            if (_logger.IsTrace) _logger.Trace($"Added a transaction: {transaction.Hash}");
        }

        private void OwnTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (_ownTransactions.Count > 0)
            {
                foreach ((_, Transaction tx) in _ownTransactions)
                {
                    NotifyAllPeers(tx);
                }

                // we only reenable the timer if there are any transaction pending
                // otherwise adding own transaction will reenable the timer anyway
                _ownTimer.Enabled = true;
            }
        }
        
        private void RemovalTimerElapsed(object sender, ElapsedEventArgs eventArgs)
        {
            if (_pendingTransactions.Count == 0)
            {
                return;
            }

            var hashes = new List<Keccak>();
            var timestamp = new UInt256(_timestamper.EpochSeconds);
            foreach (var transaction in _pendingTransactions.Values)
            {
                if (_nonEvictableTransactions.ContainsKey(transaction.Hash))
                {
                    if (_logger.IsDebug) _logger.Debug($"Pending transaction: {transaction.Hash} will not be evicted.");
                    continue;
                }
                
                if (_pendingTxThresholdValidator.IsRemovable(timestamp, transaction.Timestamp))
                {
                    hashes.Add(transaction.Hash);
                }
            }

            for (var i = 0; i < hashes.Count; i++)
            {
                if (_pendingTransactions.TryRemove(hashes[i], out var transaction))
                {
                    RemovedPending?.Invoke(this, new TxEventArgs(transaction));
                }
            }
        }
    }
}