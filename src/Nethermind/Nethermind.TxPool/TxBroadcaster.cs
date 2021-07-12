//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Threading.Tasks;
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
        private readonly SortedPool<Keccak, Transaction, Address> _txs;

        private readonly ILogger _logger;
        
        public TxBroadcaster(IComparer<Transaction> comparer,
            ITimerFactory timerFactory,
            ITxPoolConfig txPoolConfig,
            ILogManager? logManager)
        {
            _txPoolConfig = txPoolConfig;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txs = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, comparer, logManager);

            _timer = timerFactory.CreateTimer(TimeSpan.FromMilliseconds(500));
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }

        internal Transaction[] GetSnapshot() => _txs.GetSnapshot();

        public void StartBroadcast(Transaction tx)
        {
            _txs.TryInsert(tx.Hash, tx);
        }
      
        public void BroadcastOnce(Transaction tx)
        {
            NotifySelectedPeers(tx);
        }
        
        public void BroadcastOnce(ITxPoolPeer peer, Transaction tx)
        {
            Notify(peer, tx, false);
        }
        
        public void StopBroadcast(Keccak txHash)
        {
            if (_txs.Count != 0)
            {
                bool wasIncluded = _txs.TryRemove(txHash, out Transaction _);
                if (wasIncluded)
                {
                    if (_logger.IsTrace) _logger.Trace(
                        $"Transaction {txHash} removed from broadcaster after block inclusion");
                }
            }
        }
        
        private void TimerOnElapsed(object sender, EventArgs args)
        {
            if (_txs.Count > 0)
            {
                foreach (Transaction tx in _txs.GetSnapshot())
                {
                    // note that this is parallelized outside which I would like to change
                    NotifyAllPeers(tx);
                }
            }
            
            _timer.Enabled = true;
        }

        private void Notify(ITxPoolPeer peer, Transaction tx, bool isPriority)
        {
            try
            {
                if (peer.SendNewTransaction(tx, isPriority))
                {
                    Metrics.PendingTransactionsSent++;
                    if (_logger.IsTrace) _logger.Trace($"Notified {peer} about a transaction: {tx.Hash}");
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Failed to notify {peer} about a transaction: {tx.Hash}", e);
            }
        }

        private void NotifySelectedPeers(Transaction tx)
        {
            Task.Run(() =>
            {
                foreach ((_, ITxPoolPeer peer) in _peers)
                {
                    if (tx.DeliveredBy == null)
                    {
                        Notify(peer, tx, true);
                        continue;
                    }

                    if (tx.DeliveredBy.Equals(peer.Id))
                    {
                        continue;
                    }

                    if (_txPoolConfig.PeerNotificationThreshold < Random.Value.Next(1, 100))
                    {
                        continue;
                    }

                    Notify(peer, tx, 3 < Random.Value.Next(1, 10));
                }
            });
        }

        private void NotifyAllPeers(Transaction tx)
        {
            if (_logger.IsDebug)
            {
                _logger.Debug($"Broadcasting transaction {tx.Hash} to all peers");
            }
            else if (_logger.IsTrace)
            {
                _logger.Trace($"Broadcasting transaction {tx.ToString("  ")} to all peers");
            }

            
            Task.Run(() =>
            {
                foreach ((_, ITxPoolPeer peer) in _peers)
                {
                    Notify(peer, tx, true);
                }
            });
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
