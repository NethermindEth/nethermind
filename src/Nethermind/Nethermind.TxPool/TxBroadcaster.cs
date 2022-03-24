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
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Core.Timers;
using Nethermind.Int256;
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
        private readonly IChainHeadInfoProvider _headInfo;

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
        private ResettableList<Transaction> _accumulatedTemporaryTxs;

        /// <summary>
        /// Bag for exchanging with _accumulatedTemporaryTxs and for preparing sending message.
        /// </summary>
        private ResettableList<Transaction> _txsToSend;

        private readonly ILogger _logger;

        public TxBroadcaster(IComparer<Transaction> comparer,
            ITimerFactory timerFactory,
            ITxPoolConfig txPoolConfig,
            IChainHeadInfoProvider chainHeadInfoProvider,
            ILogManager? logManager)
        {
            _txPoolConfig = txPoolConfig;
            _headInfo = chainHeadInfoProvider;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _persistentTxs = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, comparer, logManager);
            _accumulatedTemporaryTxs = new ResettableList<Transaction>(512, 4);
            _txsToSend = new ResettableList<Transaction>(512, 4);

            _timer = timerFactory.CreateTimer(TimeSpan.FromMilliseconds(1000));
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }

        internal Transaction[] GetSnapshot() => _persistentTxs.GetSnapshot();

        public void Broadcast(Transaction tx, bool isPersistent)
        {
            if (isPersistent)
            {
                StartBroadcast(tx);
            }
            else
            {
                BroadcastOnce(tx);
            }
        }

        private void StartBroadcast(Transaction tx)
        {
            NotifyPeersAboutLocalTx(tx);
            _persistentTxs.TryInsert(tx.Hash, tx);
        }

        private void BroadcastOnce(Transaction tx)
        {
            lock (_accumulatedTemporaryTxs)
            {
                _accumulatedTemporaryTxs.Add(tx);
            }
        }
        
        public void BroadcastOnce(ITxPoolPeer peer, Transaction[] txs)
        {
            Notify(peer, txs, false);
        }
        
        public void BroadcastPersistentTxs()
        {
            if (_logger.IsDebug) _logger.Debug($"Broadcasting persistent transactions to all peers");

            foreach ((_, ITxPoolPeer peer) in _peers)
            {
                Notify(peer, GetPersistentTxsToSend(), true);
            }
        }
        
        internal IEnumerable<Transaction> GetPersistentTxsToSend()
        {
            if (_txPoolConfig.PeerNotificationThreshold > 0)
            {
                // PeerNotificationThreshold is a declared in config percent of transactions in persistent broadcast,
                // which will be sent when timer elapse. numberOfPersistentTxsToBroadcast is equal to
                // PeerNotificationThreshold multiplication by number of transactions in persistent broadcast, rounded up.
                int numberOfPersistentTxsToBroadcast =
                    Math.Min(_txPoolConfig.PeerNotificationThreshold * _persistentTxs.Count / 100 + 1,
                        _persistentTxs.Count);

                foreach (Transaction tx in _persistentTxs.GetFirsts())
                {
                    if (numberOfPersistentTxsToBroadcast > 0)
                    {
                        if (tx.MaxFeePerGas >= _headInfo.CurrentBaseFee)
                        {
                            numberOfPersistentTxsToBroadcast--;
                            yield return tx;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }
        
        public void StopBroadcast(Keccak txHash)
        {
            if (_persistentTxs.Count != 0)
            {
                bool hasBeenRemoved = _persistentTxs.TryRemove(txHash, out Transaction _);
                if (hasBeenRemoved)
                {
                    if (_logger.IsTrace) _logger.Trace(
                        $"Transaction {txHash} removed from broadcaster");
                }
            }
        }

        public void EnsureStopBroadcastUpToNonce(Address address, UInt256 nonce)
        {
            if (_persistentTxs.Count != 0)
            {
                foreach (Transaction tx in _persistentTxs.TakeWhile(address, t => t.Nonce <= nonce))
                {
                    StopBroadcast(tx.Hash!);
                }
            }
        }
        
        private void TimerOnElapsed(object sender, EventArgs args)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void NotifyPeers()
            {
                _txsToSend = Interlocked.Exchange(ref _accumulatedTemporaryTxs, _txsToSend);
            
                if (_logger.IsDebug) _logger.Debug($"Broadcasting transactions to all peers");

                foreach ((_, ITxPoolPeer peer) in _peers)
                {
                    Notify(peer, GetTxsToSend(peer, _txsToSend), false);
                }

                _txsToSend.Reset();
            }
            
            NotifyPeers();
            _timer.Enabled = true;
        }

        private IEnumerable<Transaction> GetTxsToSend(ITxPoolPeer peer, IList<Transaction> txsToSend)
        {
            for (int index = 0; index < txsToSend.Count; index++)
            {
                Transaction tx = txsToSend[index];
                if (tx.DeliveredBy is null || !tx.DeliveredBy.Equals(peer.Id))
                {
                    yield return tx;
                }
            }
        }

        private void Notify(ITxPoolPeer peer, IEnumerable<Transaction> txs, bool sendFullTx)
        {
            try
            {
                peer.SendNewTransactions(txs, sendFullTx);
                if (_logger.IsTrace) _logger.Trace($"Notified {peer} about transactions.");
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Failed to notify {peer} about transactions.", e);
            }
        }
        
        private void NotifyPeersAboutLocalTx(Transaction tx)
        {
            if (_logger.IsDebug) _logger.Debug($"Broadcasting new local transaction {tx.Hash} to all peers");

            foreach ((_, ITxPoolPeer peer) in _peers)
            {
                try
                {
                    peer.SendNewTransaction(tx);
                    if (_logger.IsTrace) _logger.Trace($"Notified {peer} about transaction {tx.Hash}.");
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Failed to notify {peer} about transaction {tx.Hash}.", e);
                }
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
