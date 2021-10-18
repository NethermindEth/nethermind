﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool
{
    /// <summary>
    /// This class responsibility is to notify other peers about interesting transactions.
    /// </summary>
    internal class TxBroadcaster : IDisposable
    {
        private readonly ITxPoolConfig _txPoolConfig;

        /// <summary>
        /// Notification threshold randomizer seed
        /// </summary>
        private static int _seed = Environment.TickCount;

        /// <summary>
        /// Random number generator for peer notification threshold - no need to be securely random.
        /// </summary>
        private static readonly ThreadLocal<Random> Random =
            new(() => new Random(Interlocked.Increment(ref _seed)));
        
        /// <summary>
        /// Timer for rebroadcasting pending own transactions.
        /// </summary>
        private readonly ITimer _timer;
        
        /// <summary>
        /// Connected peers that can be notified about transactions.
        /// </summary>
        private readonly ConcurrentDictionary<PublicKey, ITxPoolPeer> _peers = new();
        
        /// <summary>
        /// Transactions published locally (initiated by this node users) or reorganised.
        /// </summary>
        private readonly SortedPool<Keccak, Transaction, Address> _persistentTxs;
        
        /// <summary>
        /// Transactions added by external peers between timer elapses.
        /// </summary>
        private ConcurrentBag<Transaction> _accumulatedTemporaryTxs;

        /// <summary>
        /// Bag for exchanging with _accumulatedTemporaryTxs and for preparing sending message.
        /// </summary>
        private ConcurrentBag<Transaction> _txsToSend;

        private readonly ILogger _logger;

        public TxBroadcaster(IComparer<Transaction> comparer,
            ITimerFactory timerFactory,
            ITxPoolConfig txPoolConfig,
            ILogManager? logManager)
        {
            _txPoolConfig = txPoolConfig;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _persistentTxs = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, comparer, logManager);
            _accumulatedTemporaryTxs = new ConcurrentBag<Transaction>();
            _txsToSend = new ConcurrentBag<Transaction>();

            _timer = timerFactory.CreateTimer(TimeSpan.FromMilliseconds(1000));
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }

        internal Transaction[] GetSnapshot() => _persistentTxs.GetSnapshot();

        public void StartBroadcast(Transaction tx)
        {
            _persistentTxs.TryInsert(tx.Hash, tx);
        }
      
        public void BroadcastOnce(Transaction tx)
        {
            _accumulatedTemporaryTxs.Add(tx);
        }
        
        public void BroadcastOnce(ITxPoolPeer peer, Transaction[] txs)
        {
            Notify(peer, txs);
        }
        
        public void StopBroadcast(Keccak txHash)
        {
            if (_persistentTxs.Count != 0)
            {
                bool wasIncluded = _persistentTxs.TryRemove(txHash, out Transaction _);
                if (wasIncluded)
                {
                    if (_logger.IsTrace) _logger.Trace(
                        $"Transaction {txHash} removed from broadcaster after block inclusion");
                }
            }
        }
        
        private void TimerOnElapsed(object sender, EventArgs args)
        {
            void NotifyPeers()
            {
                Transaction[] persistentTxs = _persistentTxs.GetSnapshot();

                _txsToSend = Interlocked.Exchange(ref _accumulatedTemporaryTxs, _txsToSend);
            
                foreach ((_, ITxPoolPeer peer) in _peers)
                {
                    if (_logger.IsDebug) _logger.Debug($"Broadcasting transactions to all peers");

                    Notify(peer, GetTxsToSend(peer, persistentTxs, _txsToSend));
                }

                _txsToSend.Clear();
            }
            
            NotifyPeers();
            _timer.Enabled = true;
        }

        private IEnumerable<Transaction> GetTxsToSend(ITxPoolPeer peer, IReadOnlyList<Transaction> persistentTxs, IEnumerable<Transaction> txsToSend)
        {
            for (int i = 0; i < persistentTxs.Count; i++)
            {
                if (_txPoolConfig.PeerNotificationThreshold >= Random.Value.Next(1, 100))
                {
                    yield return persistentTxs[i];
                }
            }

            foreach (Transaction tx in txsToSend)
            {
                if (tx.DeliveredBy is null || !tx.DeliveredBy.Equals(peer.Id))
                {
                    yield return tx;
                }
            }
        }

        private void Notify(ITxPoolPeer peer, IEnumerable<Transaction> txs)
        {
            try
            {
                peer.SendNewTransactions(txs);
                if (_logger.IsTrace) _logger.Trace($"Notified {peer} about transactions.");
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Failed to notify {peer} about transactions.", e);
            }
        }

        public bool AddPeer(ITxPoolPeer peer)
        {
            return _peers.TryAdd(peer.Id, peer);
        }

        public bool RemovePeer(PublicKey nodeId)
        {
            return _peers.TryRemove(nodeId, out _);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
