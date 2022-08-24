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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.TxPool
{
    /// <summary>
    /// Stores all pending transactions. These will be used by block producer if this node is a miner / validator
    /// or simply for broadcasting and tracing in other cases.
    /// </summary>
    public partial class TxPool : ITxPool, IDisposable
    {
        private readonly object _locker = new();

        private readonly ConcurrentDictionary<Address, AddressNonces> _nonces = new();

        private readonly List<IIncomingTxFilter> _filterPipeline = new();

        private readonly HashCache _hashCache = new();

        private readonly TxBroadcaster _broadcaster;

        private readonly TxDistinctSortedPool _transactions;

        private readonly IChainHeadSpecProvider _specProvider;

        private readonly IAccountStateProvider _accounts;

        private readonly IChainHeadInfoProvider _headInfo;
        private readonly ITxPoolConfig _txPoolConfig;

        private readonly ILogger _logger;

        private readonly Channel<BlockReplacementEventArgs> _headBlocksChannel = Channel.CreateUnbounded<BlockReplacementEventArgs>(new UnboundedChannelOptions() { SingleReader = true, SingleWriter = true });

        /// <summary>
        /// Indexes transactions
        /// </summary>
        private ulong _txIndex;


        /// <summary>
        /// This class stores all known pending transactions that can be used for block production
        /// (by miners or validators) or simply informing other nodes about known pending transactions (broadcasting).
        /// </summary>
        /// <param name="txStorage">Tx storage used to reject known transactions.</param>
        /// <param name="ecdsa">Used to recover sender addresses from transaction signatures.</param>
        /// <param name="chainHeadInfoProvider"></param>
        /// <param name="txPoolConfig"></param>
        /// <param name="validator"></param>
        /// <param name="logManager"></param>
        /// <param name="comparer"></param>
        /// <param name="incomingTxFilter"></param>
        public TxPool(
            IEthereumEcdsa ecdsa,
            IChainHeadInfoProvider chainHeadInfoProvider,
            ITxPoolConfig txPoolConfig,
            ITxValidator validator,
            ILogManager? logManager,
            IComparer<Transaction> comparer,
            IIncomingTxFilter? incomingTxFilter = null)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _headInfo = chainHeadInfoProvider ?? throw new ArgumentNullException(nameof(chainHeadInfoProvider));
            _txPoolConfig = txPoolConfig;
            _accounts = _headInfo.AccountStateProvider;
            _specProvider = _headInfo.SpecProvider;


            MemoryAllowance.MemPoolSize = txPoolConfig.Size;
            AddNodeInfoEntryForTxPool();

            _transactions = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, comparer, logManager);
            _broadcaster = new TxBroadcaster(comparer, TimerFactory.Default, txPoolConfig, chainHeadInfoProvider, logManager);

            _headInfo.HeadChanged += OnHeadChange;

            _filterPipeline.Add(new NullHashTxFilter());
            _filterPipeline.Add(new AlreadyKnownTxFilter(_hashCache));
            _filterPipeline.Add(new MalformedTxFilter(_specProvider, validator, _logger));
            _filterPipeline.Add(new GasLimitTxFilter(_headInfo, txPoolConfig, _logger));
            _filterPipeline.Add(new UnknownSenderFilter(ecdsa, _logger));
            _filterPipeline.Add(new DeployedCodeFilter(_specProvider, _accounts)); // has to be after UnknownSenderFilter as it uses sender
            _filterPipeline.Add(new LowNonceFilter(_accounts, _logger));
            _filterPipeline.Add(new GapNonceFilter(_accounts, _transactions, _logger));
            _filterPipeline.Add(new TooExpensiveTxFilter(_headInfo, _accounts, _transactions, _logger));
            _filterPipeline.Add(new FeeTooLowFilter(_headInfo, _accounts, _transactions, logManager));
            _filterPipeline.Add(new ReusedOwnNonceTxFilter(_accounts, _nonces, _logger));
            if (incomingTxFilter is not null)
            {
                _filterPipeline.Add(incomingTxFilter);
            }

            ProcessNewHeads();
        }

        public Transaction[] GetPendingTransactions() => _transactions.GetSnapshot();

        public int GetPendingTransactionsCount() => _transactions.Count;

        public IDictionary<Address, Transaction[]> GetPendingTransactionsBySender() =>
            _transactions.GetBucketSnapshot();
        
        public Transaction[] GetPendingTransactionsBySender(Address address) =>
            _transactions.GetBucketSnapshot(address);

        internal Transaction[] GetOwnPendingTransactions() => _broadcaster.GetSnapshot();

        private void OnHeadChange(object? sender, BlockReplacementEventArgs e)
        {
            // TODO: I think this is dangerous if many blocks are processed one after another
            try
            {
                _hashCache.ClearCurrentBlockCache();
                _headBlocksChannel.Writer.TryWrite(e);
            }
            catch (Exception exception)
            {
                if (_logger.IsError)
                    _logger.Error(
                        $"Couldn't correctly add or remove transactions from txpool after processing block {e.Block!.ToString(Block.Format.FullHashAndNumber)}.", exception);
            }
        }

        private void ProcessNewHeads()
        {
            Task.Factory.StartNew(async () =>
            {
                while (await _headBlocksChannel.Reader.WaitToReadAsync())
                {
                    while (_headBlocksChannel.Reader.TryRead(out BlockReplacementEventArgs args))
                    {
                        try
                        {
                            ReAddReorganisedTransactions(args.PreviousBlock);
                            RemoveProcessedTransactions(args.Block.Transactions);
                            UpdateBuckets();
                            _broadcaster.BroadcastPersistentTxs();
                            Metrics.TransactionCount = _transactions.Count;
                        }
                        catch (Exception e)
                        {
                            if (_logger.IsDebug) _logger.Debug($"TxPool failed to update after block {args.Block.ToString(Block.Format.FullHashAndNumber)} with exception {e}");
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error($"TxPool update after block queue failed.", t.Exception);
                }
            });
        }

        private void ReAddReorganisedTransactions(Block? previousBlock)
        {
            if (previousBlock is not null)
            {
                bool isEip155Enabled = _specProvider.GetSpec(previousBlock.Number).IsEip155Enabled;
                for (int i = 0; i < previousBlock.Transactions.Length; i++)
                {
                    Transaction tx = previousBlock.Transactions[i];
                    _hashCache.Delete(tx.Hash!);
                    SubmitTx(tx, isEip155Enabled ? TxHandlingOptions.None : TxHandlingOptions.PreEip155Signing);
                }
            }
        }

        private void RemoveProcessedTransactions(IReadOnlyList<Transaction> blockTransactions)
        {
            long transactionsInBlock = blockTransactions.Count;
            long discoveredForPendingTxs = 0;
            long discoveredForHashCache = 0;
            long eip1559Txs = 0;

            for (int i = 0; i < transactionsInBlock; i++)
            {
                Keccak txHash = blockTransactions[i].Hash;

                if (!IsKnown(txHash!))
                {
                    discoveredForHashCache++;
                }

                if (!RemoveIncludedTransaction(blockTransactions[i]))
                {
                    discoveredForPendingTxs++;
                }

                if (blockTransactions[i].IsEip1559)
                {
                    eip1559Txs++;
                }
            }

            if (transactionsInBlock != 0)
            {
                Metrics.DarkPoolRatioLevel1 = (float)discoveredForHashCache / transactionsInBlock;
                Metrics.DarkPoolRatioLevel2 = (float)discoveredForPendingTxs / transactionsInBlock;
                Metrics.Eip1559TransactionsRatio = (float)eip1559Txs / transactionsInBlock;
            }
        }

        private bool RemoveIncludedTransaction(Transaction tx)
        {
            bool removed = RemoveTransaction(tx.Hash);
            _broadcaster.EnsureStopBroadcastUpToNonce(tx.SenderAddress!, tx.Nonce);
            return removed;
        }

        public void AddPeer(ITxPoolPeer peer)
        {
            PeerInfo peerInfo = new(peer);
            if (_broadcaster.AddPeer(peerInfo))
            {
                _broadcaster.BroadcastOnce(peerInfo, _transactions.GetSnapshot());

                if (_logger.IsTrace) _logger.Trace($"Added a peer to TX pool: {peer}");
            }
        }

        public void RemovePeer(PublicKey nodeId)
        {
            if (_broadcaster.RemovePeer(nodeId))
            {
                if (_logger.IsTrace) _logger.Trace($"Removed a peer from TX pool: {nodeId}");
            }
        }

        public AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions handlingOptions)
        {
            Metrics.PendingTransactionsReceived++;

            // assign a sequence number to transaction so we can order them by arrival times when
            // gas prices are exactly the same
            tx.PoolIndex = Interlocked.Increment(ref _txIndex);

            NewDiscovered?.Invoke(this, new TxEventArgs(tx));

            bool managedNonce = (handlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
            bool startBroadcast = (handlingOptions & TxHandlingOptions.PersistentBroadcast) ==
                                  TxHandlingOptions.PersistentBroadcast;

            if (_logger.IsTrace)
                _logger.Trace(
                    $"Adding transaction {tx.ToString("  ")} - managed nonce: {managedNonce} | persistent broadcast {startBroadcast}");

            for (int i = 0; i < _filterPipeline.Count; i++)
            {
                IIncomingTxFilter incomingTxFilter = _filterPipeline[i];
                AcceptTxResult accepted = incomingTxFilter.Accept(tx, handlingOptions);
                if (!accepted)
                {
                    Metrics.PendingTransactionsDiscarded++;
                    return accepted;
                }
            }

            return AddCore(tx, startBroadcast);
        }

        private AcceptTxResult AddCore(Transaction tx, bool isPersistentBroadcast)
        {
            lock (_locker)
            {
                bool eip1559Enabled = _specProvider.GetCurrentHeadSpec().IsEip1559Enabled;

                tx.GasBottleneck = tx.CalculateEffectiveGasPrice(eip1559Enabled, _headInfo.CurrentBaseFee);
                bool inserted = _transactions.TryInsert(tx.Hash, tx, out Transaction? removed);
                if (inserted)
                {
                    _transactions.UpdateGroup(tx.SenderAddress!, UpdateBucketWithAddedTransaction);
                    Metrics.PendingTransactionsAdded++;
                    if (tx.IsEip1559) { Metrics.Pending1559TransactionsAdded++; }

                    if (removed != null)
                    {
                        EvictedPending?.Invoke(this, new TxEventArgs(removed));
                        // transaction which was on last position in sorted TxPool and was deleted to give
                        // a place for a newly added tx (with higher priority) is now removed from hashCache
                        // to give it opportunity to come back to TxPool in the future, when fees drops
                        _hashCache.DeleteFromLongTerm(removed.Hash!);
                        Metrics.PendingTransactionsEvicted++;
                    }
                }
                else
                {
                    Metrics.PendingTransactionsTooLowFee++;
                    return AcceptTxResult.FeeTooLowToCompete;
                }
            }

            _broadcaster.Broadcast(tx, isPersistentBroadcast);

            _hashCache.SetLongTerm(tx.Hash!);
            NewPending?.Invoke(this, new TxEventArgs(tx));
            Metrics.TransactionCount = _transactions.Count;
            return AcceptTxResult.Accepted;
        }

        private IEnumerable<(Transaction Tx, Action<Transaction>? Change)> UpdateBucketWithAddedTransaction(
            Address address, IReadOnlyCollection<Transaction> transactions)
        {
            if (transactions.Count != 0)
            {
                Account account = _accounts.GetAccount(address);
                UInt256 balance = account.Balance;
                long currentNonce = (long)(account.Nonce);

                foreach (var changedTx in UpdateGasBottleneck(transactions, currentNonce, balance))
                {
                    yield return changedTx;
                }
            }
        }

        private IEnumerable<(Transaction Tx, Action<Transaction>? Change)> UpdateGasBottleneck(
            IReadOnlyCollection<Transaction> transactions, long currentNonce, UInt256 balance)
        {
            UInt256? previousTxBottleneck = null;
            int i = 0;

            foreach (Transaction tx in transactions)
            {
                UInt256 gasBottleneck = 0;

                if (tx.Nonce < currentNonce)
                {
                    _broadcaster.StopBroadcast(tx.Hash!);
                    yield return (tx, null);
                }
                else
                {
                    if (previousTxBottleneck == null)
                    {
                        previousTxBottleneck = tx.CalculateAffordableGasPrice(_specProvider.GetCurrentHeadSpec().IsEip1559Enabled,
                            _headInfo.CurrentBaseFee, balance);
                    }

                    if (tx.Nonce == currentNonce + i)
                    {
                        UInt256 effectiveGasPrice =
                            tx.CalculateEffectiveGasPrice(_specProvider.GetCurrentHeadSpec().IsEip1559Enabled,
                                _headInfo.CurrentBaseFee);
                        gasBottleneck = UInt256.Min(effectiveGasPrice, previousTxBottleneck ?? 0);
                    }

                    if (tx.GasBottleneck != gasBottleneck)
                    {
                        yield return (tx, SetGasBottleneckChange(gasBottleneck));
                    }

                    previousTxBottleneck = gasBottleneck;
                    i++;
                }
            }
        }

        private static Action<Transaction> SetGasBottleneckChange(UInt256 gasBottleneck)
        {
            return t => t.GasBottleneck = gasBottleneck;
        }

        private void UpdateBuckets()
        {
            lock (_locker)
            {
                // ensure the capacity of the pool
                if (_transactions.Count > _txPoolConfig.Size)
                    if (_logger.IsWarn) _logger.Warn($"TxPool exceeds the config size {_transactions.Count}/{_txPoolConfig.Size}");
                _transactions.UpdatePool(UpdateBucket);
            }
        }

        private IEnumerable<(Transaction Tx, Action<Transaction>? Change)> UpdateBucket(Address address, IReadOnlyCollection<Transaction> transactions)
        {
            if (transactions.Count != 0)
            {
                Account? account = _accounts.GetAccount(address);
                UInt256 balance = account.Balance;
                long currentNonce = (long)(account.Nonce);
                Transaction tx = transactions.FirstOrDefault(t => t.Nonce == currentNonce);
                bool shouldBeDumped = false;

                if (tx is null)
                {
                    shouldBeDumped = true;
                }
                else if (balance < tx.Value)
                {
                    shouldBeDumped = true;
                }
                else if (!tx.IsEip1559)
                {
                    shouldBeDumped = UInt256.MultiplyOverflow(tx.GasPrice, (UInt256)tx.GasLimit, out UInt256 cost);
                    shouldBeDumped |= UInt256.AddOverflow(cost, tx.Value, out cost);
                    shouldBeDumped |= balance < cost;
                }

                if (shouldBeDumped)
                {
                    foreach (Transaction transaction in transactions)
                    {
                        // transaction removed from TxPool because of insufficient balance should have opportunity
                        // to come back in the future, so it is removed from long term cache as well.
                        _hashCache.DeleteFromLongTerm(transaction.Hash!);
                        yield return (transaction, null);
                    }
                }
                else
                {
                    foreach (var changedTx in UpdateGasBottleneck(transactions, currentNonce, balance))
                    {
                        yield return changedTx;
                    }
                }
            }
        }

        public bool RemoveTransaction(Keccak? hash)
        {
            if (hash is null)
            {
                return false;
            }

            bool hasBeenRemoved;
            lock (_locker)
            {
                hasBeenRemoved = _transactions.TryRemove(hash, out Transaction transaction);
                if (hasBeenRemoved)
                {
                    Address address = transaction.SenderAddress;
                    if (_nonces.TryGetValue(address!, out AddressNonces addressNonces))
                    {
                        addressNonces.Nonces.TryRemove(transaction.Nonce, out _);
                        if (addressNonces.Nonces.IsEmpty)
                        {
                            _nonces.Remove(address, out _);
                        }
                    }

                    RemovedPending?.Invoke(this, new TxEventArgs(transaction));
                }

                _broadcaster.StopBroadcast(hash);
            }

            if (_logger.IsTrace) _logger.Trace($"Removed a transaction: {hash}");

            return hasBeenRemoved;
        }

        public bool TryGetPendingTransaction(Keccak hash, out Transaction transaction)
        {
            lock (_locker)
            {
                if (!_transactions.TryGetValue(hash, out transaction))
                {
                    _broadcaster.TryGetPersistentTx(hash, out transaction);

                    // commented out as it puts too much pressure on the database
                    // and it not really required in any scenario
                    // * tx recovery usually will fetch from pending
                    // * get tx via RPC usually will fetch from block or from pending
                    // * internal tx pool scenarios are handled directly elsewhere
                    // transaction = _txStorage.Get(hash);
                }
            }

            return transaction != null;
        }

        // TODO: Ensure that nonce is always valid in case of sending own transactions from different nodes.
        public UInt256 ReserveOwnTransactionNonce(Address address)
        {
            UInt256 currentNonce = 0;
            _nonces.AddOrUpdate(address, a =>
            {
                currentNonce = _accounts.GetAccount(address).Nonce;
                return new AddressNonces(currentNonce);
            }, (a, n) =>
            {
                currentNonce = n.ReserveNonce().Value;
                return n;
            });

            return currentNonce;
        }

        public UInt256 GetLatestPendingNonce(Address address)
        {
            UInt256 maxPendingNonce = _accounts.GetAccount(address).Nonce;

            // we are not doing any updating, but lets just use a thread-safe method without any data copying like snapshot
            _transactions.UpdateGroup(address, (_, transactions) =>
            {
                // This is under the assumption that the addressTransactions are sorted by Nonce.
                if (transactions.Count > 0)
                {
                    // if we don't have any gaps we can easily calculate the nonce
                    Transaction lastTransaction = transactions.Max!;
                    if (maxPendingNonce + (UInt256)transactions.Count - 1 == lastTransaction.Nonce)
                    {
                        maxPendingNonce = lastTransaction.Nonce + 1;
                    }

                    // we have a gap, need to scan the transactions
                    else
                    {
                        foreach (Transaction transaction in transactions)
                        {
                            if (transaction.Nonce == maxPendingNonce)
                            {
                                maxPendingNonce++;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }

                // we won't do any actual changes
                return Array.Empty<(Transaction Tx, Action<Transaction>? Change)>();
            });

            return maxPendingNonce;
        }

        public bool IsKnown(Keccak hash) => _hashCache.Get(hash);

        public event EventHandler<TxEventArgs>? NewDiscovered;
        public event EventHandler<TxEventArgs>? NewPending;
        public event EventHandler<TxEventArgs>? RemovedPending;
        public event EventHandler<TxEventArgs>? EvictedPending;

        public void Dispose()
        {
            _broadcaster.Dispose();
            _headInfo.HeadChanged -= OnHeadChange;
            _headBlocksChannel.Writer.Complete();
        }

        /// <summary>
        /// This method is used just for nice logging features in the console.
        /// </summary>
        private static void AddNodeInfoEntryForTxPool()
        {
            ThisNodeInfo.AddInfo("Mem est tx   :",
                $"{(LruCache<Keccak, object>.CalculateMemorySize(32, MemoryAllowance.TxHashCacheSize) + LruCache<Keccak, Transaction>.CalculateMemorySize(4096, MemoryAllowance.MemPoolSize)) / 1000 / 1000}MB"
                    .PadLeft(8));
        }
    }
}
