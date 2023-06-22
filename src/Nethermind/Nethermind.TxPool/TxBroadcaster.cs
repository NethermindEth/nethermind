// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        private readonly ITxGossipPolicy _txGossipPolicy;

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
        private readonly SortedPool<ValueKeccak, Transaction, Address> _persistentTxs;

        /// <summary>
        /// Transactions added by external peers between timer elapses.
        /// </summary>
        private ResettableList<Transaction> _accumulatedTemporaryTxs;

        /// <summary>
        /// Bag for exchanging with _accumulatedTemporaryTxs and for preparing sending message.
        /// </summary>
        private ResettableList<Transaction> _txsToSend;

        /// <summary>
        /// Used to throttle tx broadcast. Particularly during forward sync where the head changes a lot which triggers
        /// a lot of broadcast. There are no transaction in pool but its quite spammy on the log.
        /// </summary>
        private DateTimeOffset _lastPersistedTxBroadcast = DateTimeOffset.UnixEpoch;
        private readonly TimeSpan _minTimeBetweenPersistedTxBroadcast = TimeSpan.FromSeconds(1);

        private readonly ILogger _logger;

        public TxBroadcaster(IComparer<Transaction> comparer,
            ITimerFactory timerFactory,
            ITxPoolConfig txPoolConfig,
            IChainHeadInfoProvider chainHeadInfoProvider,
            ILogManager? logManager,
            ITxGossipPolicy? transactionsGossipPolicy = null)
        {
            _txPoolConfig = txPoolConfig;
            _headInfo = chainHeadInfoProvider;
            _txGossipPolicy = transactionsGossipPolicy ?? ShouldGossip.Instance;
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
            if (tx.Hash is not null)
            {
                _persistentTxs.TryInsert(tx.Hash, tx);
            }
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
            if (_persistentTxs.Count == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"There is nothing to broadcast - collection of persistent txs is empty");
                return;
            }

            DateTimeOffset now = DateTimeOffset.Now;
            if (_lastPersistedTxBroadcast + _minTimeBetweenPersistedTxBroadcast > now)
            {
                if (_logger.IsTrace) _logger.Trace($"Minimum time between persistent tx broadcast not reached.");
                return;
            }
            _lastPersistedTxBroadcast = now;

            if (_txPoolConfig.PeerNotificationThreshold > 0)
            {
                (IList<Transaction>? transactionsToSend, IList<Transaction>? hashesToSend) = GetPersistentTxsToSend();

                if (transactionsToSend is not null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Broadcasting {transactionsToSend.Count} persistent transactions to all peers.");

                    foreach ((_, ITxPoolPeer peer) in _peers)
                    {
                        Notify(peer, transactionsToSend, true);
                    }
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug($"There are currently no transactions able to broadcast.");
                }

                if (hashesToSend is not null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Announcing {hashesToSend.Count} hashes of persistent transactions to all peers.");

                    foreach ((_, ITxPoolPeer peer) in _peers)
                    {
                        Notify(peer, hashesToSend, false);
                    }
                }
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"PeerNotificationThreshold is not a positive value: {_txPoolConfig.PeerNotificationThreshold}. Skipping broadcasting persistent transactions.");
            }
        }

        internal (IList<Transaction>? TransactionsToSend, IList<Transaction>? HashesToSend) GetPersistentTxsToSend()
        {
            // PeerNotificationThreshold is a declared in config max percent of transactions in persistent broadcast,
            // which will be sent after processing of every block. numberOfPersistentTxsToBroadcast is equal to
            // PeerNotificationThreshold multiplication by number of transactions in persistent broadcast, rounded up.
            int numberOfPersistentTxsToBroadcast =
                Math.Min(_txPoolConfig.PeerNotificationThreshold * _persistentTxs.Count / 100 + 1, _persistentTxs.Count);

            List<Transaction>? persistentTxsToSend = null;
            List<Transaction>? persistentHashesToSend = null;

            foreach (Transaction tx in _persistentTxs.GetFirsts())
            {
                if (numberOfPersistentTxsToBroadcast > 0)
                {
                    if (tx.MaxFeePerGas >= _headInfo.CurrentBaseFee)
                    {
                        numberOfPersistentTxsToBroadcast--;
                        if (tx.CanBeBroadcast())
                        {
                            persistentTxsToSend ??= new List<Transaction>(numberOfPersistentTxsToBroadcast);
                            persistentTxsToSend.Add(tx);
                        }
                        else
                        {
                            persistentHashesToSend ??= new List<Transaction>(numberOfPersistentTxsToBroadcast);
                            persistentHashesToSend.Add(tx);
                        }
                    }
                }
                else
                {
                    break;
                }
            }

            return (persistentTxsToSend, persistentHashesToSend);
        }

        public void StopBroadcast(Keccak txHash)
        {
            if (_persistentTxs.Count != 0)
            {
                bool hasBeenRemoved = _persistentTxs.TryRemove(txHash, out Transaction? _);
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

        private void TimerOnElapsed(object? sender, EventArgs args)
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void NotifyPeers()
            {
                _txsToSend = Interlocked.Exchange(ref _accumulatedTemporaryTxs, _txsToSend);

                if (_logger.IsDebug) _logger.Debug($"Broadcasting transactions to all peers");

                foreach ((_, ITxPoolPeer peer) in _peers)
                {
                    Notify(peer, _txsToSend, false);
                }

                _txsToSend.Reset();
            }

            NotifyPeers();
            _timer.Enabled = true;
        }


        private void Notify(ITxPoolPeer peer, IEnumerable<Transaction> txs, bool sendFullTx)
        {
            if (_txGossipPolicy.CanGossipTransactions)
            {
                try
                {

                    peer.SendNewTransactions(txs.Where(t => _txGossipPolicy.ShouldGossipTransaction(t)), sendFullTx);
                    if (_logger.IsTrace) _logger.Trace($"Notified {peer} about transactions.");
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Failed to notify {peer} about transactions.", e);
                }
            }
        }

        private void NotifyPeersAboutLocalTx(Transaction tx)
        {
            if (!_txGossipPolicy.CanGossipTransactions || !_txGossipPolicy.ShouldGossipTransaction(tx)) return;

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

        public bool TryGetPersistentTx(Keccak hash, out Transaction? transaction)
        {
            return _persistentTxs.TryGetValue(hash, out transaction);
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
