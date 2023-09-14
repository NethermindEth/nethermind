// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
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
    public class TxPool : ITxPool, IDisposable
    {
        private readonly object _locker = new();

        private readonly IIncomingTxFilter[] _preHashFilters;
        private readonly IIncomingTxFilter[] _postHashFilters;

        private readonly HashCache _hashCache = new();
        private readonly TxBroadcaster _broadcaster;

        private readonly TxDistinctSortedPool _transactions;
        private readonly BlobTxDistinctSortedPool _blobTransactions;

        private readonly IChainHeadSpecProvider _specProvider;
        private readonly IAccountStateProvider _accounts;
        private readonly IChainHeadInfoProvider _headInfo;
        private readonly ITxPoolConfig _txPoolConfig;

        private readonly ILogger _logger;

        private readonly Channel<BlockReplacementEventArgs> _headBlocksChannel = Channel.CreateUnbounded<BlockReplacementEventArgs>(new UnboundedChannelOptions() { SingleReader = true, SingleWriter = true });

        private readonly Func<Address, Account, EnhancedSortedSet<Transaction>, IEnumerable<(Transaction Tx, UInt256? changedGasBottleneck)>> _updateBucket;

        /// <summary>
        /// Indexes transactions
        /// </summary>
        private ulong _txIndex;

        private readonly ITimer? _timer;
        private Transaction[]? _transactionSnapshot;
        private Transaction[]? _blobTransactionSnapshot;

        /// <summary>
        /// This class stores all known pending transactions that can be used for block production
        /// (by miners or validators) or simply informing other nodes about known pending transactions (broadcasting).
        /// </summary>
        /// <param name="ecdsa">Used to recover sender addresses from transaction signatures.</param>
        /// <param name="blobTxStorage"></param>
        /// <param name="chainHeadInfoProvider"></param>
        /// <param name="txPoolConfig"></param>
        /// <param name="validator"></param>
        /// <param name="logManager"></param>
        /// <param name="comparer"></param>
        /// <param name="transactionsGossipPolicy"></param>
        /// <param name="incomingTxFilter"></param>
        /// <param name="thereIsPriorityContract"></param>
        public TxPool(IEthereumEcdsa ecdsa,
            ITxStorage blobTxStorage,
            IChainHeadInfoProvider chainHeadInfoProvider,
            ITxPoolConfig txPoolConfig,
            ITxValidator validator,
            ILogManager? logManager,
            IComparer<Transaction> comparer,
            ITxGossipPolicy? transactionsGossipPolicy = null,
            IIncomingTxFilter? incomingTxFilter = null,
            bool thereIsPriorityContract = false)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _headInfo = chainHeadInfoProvider ?? throw new ArgumentNullException(nameof(chainHeadInfoProvider));
            _txPoolConfig = txPoolConfig;
            _accounts = _headInfo.AccountStateProvider;
            _specProvider = _headInfo.SpecProvider;

            MemoryAllowance.MemPoolSize = txPoolConfig.Size;
            AddNodeInfoEntryForTxPool();

            _transactions = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, comparer, logManager);
            _blobTransactions = txPoolConfig is { BlobSupportEnabled: true, PersistentBlobStorageEnabled: true }
                ? new PersistentBlobTxDistinctSortedPool(blobTxStorage, _txPoolConfig, comparer, logManager)
                : new BlobTxDistinctSortedPool(txPoolConfig.BlobSupportEnabled ? _txPoolConfig.InMemoryBlobPoolSize : 0, comparer, logManager);
            _broadcaster = new TxBroadcaster(comparer, TimerFactory.Default, txPoolConfig, chainHeadInfoProvider, logManager, transactionsGossipPolicy);

            _headInfo.HeadChanged += OnHeadChange;

            _preHashFilters = new IIncomingTxFilter[]
            {
                new NotSupportedTxFilter(txPoolConfig, _logger),
                new GasLimitTxFilter(_headInfo, txPoolConfig, _logger),
                new FeeTooLowFilter(_headInfo, _transactions, _blobTransactions, thereIsPriorityContract, _logger),
                new MalformedTxFilter(_specProvider, validator, _logger)
            };

            List<IIncomingTxFilter> postHashFilters = new()
            {
                new NullHashTxFilter(), // needs to be first as it assigns the hash
                new AlreadyKnownTxFilter(_hashCache, _logger),
                new UnknownSenderFilter(ecdsa, _logger),
                new TxTypeTxFilter(_transactions, _blobTransactions), // has to be after UnknownSenderFilter as it uses sender
                new BalanceZeroFilter(thereIsPriorityContract, _logger),
                new BalanceTooLowFilter(_transactions, _blobTransactions, _logger),
                new LowNonceFilter(_logger), // has to be after UnknownSenderFilter as it uses sender
                new GapNonceFilter(_transactions, _blobTransactions, _logger),
            };

            if (incomingTxFilter is not null)
            {
                postHashFilters.Add(incomingTxFilter);
            }

            postHashFilters.Add(new DeployedCodeFilter(_specProvider));

            _postHashFilters = postHashFilters.ToArray();

            // Capture closures once rather than per invocation
            _updateBucket = UpdateBucket;

            int? reportMinutes = txPoolConfig.ReportMinutes;
            if (_logger.IsInfo && reportMinutes.HasValue)
            {
                _timer = TimerFactory.Default.CreateTimer(TimeSpan.FromMinutes(reportMinutes.Value));
                _timer.AutoReset = false;
                _timer.Elapsed += TimerOnElapsed;
                _timer.Start();
            }

            ProcessNewHeads();
        }

        public Transaction[] GetPendingTransactions() => _transactions.GetSnapshot();

        public int GetPendingTransactionsCount() => _transactions.Count;

        public IDictionary<Address, Transaction[]> GetPendingTransactionsBySender() =>
            _transactions.GetBucketSnapshot();

        public IDictionary<Address, Transaction[]> GetPendingBlobTransactionsEquivalencesBySender() =>
            _blobTransactions.GetBucketSnapshot();

        public Transaction[] GetPendingTransactionsBySender(Address address) =>
            _transactions.GetBucketSnapshot(address);

        // only for testing reasons
        internal Transaction[] GetOwnPendingTransactions() => _broadcaster.GetSnapshot();

        public int GetPendingBlobTransactionsCount() => _blobTransactions.Count;

        private void OnHeadChange(object? sender, BlockReplacementEventArgs e)
        {
            try
            {
                // Clear snapshot
                _transactionSnapshot = null;
                _blobTransactionSnapshot = null;
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
                    while (_headBlocksChannel.Reader.TryRead(out BlockReplacementEventArgs? args))
                    {
                        try
                        {
                            ReAddReorganisedTransactions(args.PreviousBlock);
                            RemoveProcessedTransactions(args.Block.Transactions);
                            UpdateBuckets();
                            _broadcaster.BroadcastPersistentTxs();
                            Metrics.TransactionCount = _transactions.Count;
                            Metrics.BlobTransactionCount = _blobTransactions.Count;
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
                bool isEip155Enabled = _specProvider.GetSpec(previousBlock.Header).IsEip155Enabled;
                Transaction[] txs = previousBlock.Transactions;
                for (int i = 0; i < txs.Length; i++)
                {
                    Transaction tx = txs[i];
                    if (tx.SupportsBlobs)
                    {
                        continue;
                    }
                    _hashCache.Delete(tx.Hash!);
                    SubmitTx(tx, isEip155Enabled ? TxHandlingOptions.None : TxHandlingOptions.PreEip155Signing);
                }
            }
        }

        private void RemoveProcessedTransactions(Transaction[] blockTransactions)
        {
            long discoveredForPendingTxs = 0;
            long discoveredForHashCache = 0;
            long eip1559Txs = 0;
            long blobTxs = 0;
            long blobs = 0;

            for (int i = 0; i < blockTransactions.Length; i++)
            {
                Transaction transaction = blockTransactions[i];
                Keccak txHash = transaction.Hash ?? throw new ArgumentException("Hash was unexpectedly null!");

                if (!IsKnown(txHash))
                {
                    discoveredForHashCache++;
                }

                if (!RemoveIncludedTransaction(transaction))
                {
                    discoveredForPendingTxs++;
                }

                if (transaction.Supports1559)
                {
                    eip1559Txs++;
                }

                if (transaction.SupportsBlobs)
                {
                    blobTxs++;
                    blobs += transaction.BlobVersionedHashes?.Length ?? 0;
                }
            }

            long transactionsInBlock = blockTransactions.Length;
            if (transactionsInBlock != 0)
            {
                Metrics.DarkPoolRatioLevel1 = (float)discoveredForHashCache / transactionsInBlock;
                Metrics.DarkPoolRatioLevel2 = (float)discoveredForPendingTxs / transactionsInBlock;
                Metrics.Eip1559TransactionsRatio = (float)eip1559Txs / transactionsInBlock;
                Metrics.BlobTransactionsInBlock = blobTxs;
                Metrics.BlobsInBlock = blobs;
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
            if (_broadcaster.AddPeer(peer))
            {
                _broadcaster.AnnounceOnce(peer, _transactionSnapshot ??= _transactions.GetSnapshot());
                _broadcaster.AnnounceOnce(peer, _blobTransactionSnapshot ??= _blobTransactions.GetSnapshot());

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

            TxFilteringState state = new(tx, _accounts);

            AcceptTxResult accepted = FilterTransactions(tx, handlingOptions, state);

            if (!accepted)
            {
                Metrics.PendingTransactionsDiscarded++;
            }
            else
            {
                accepted = AddCore(tx, state, startBroadcast);
                if (accepted)
                {
                    // Clear proper snapshot
                    if (tx.SupportsBlobs)
                        _blobTransactionSnapshot = null;
                    else
                        _transactionSnapshot = null;
                }
            }

            return accepted;
        }

        private AcceptTxResult FilterTransactions(Transaction tx, TxHandlingOptions handlingOptions, TxFilteringState state)
        {
            IIncomingTxFilter[] filters = _preHashFilters;
            for (int i = 0; i < filters.Length; i++)
            {
                AcceptTxResult accepted = filters[i].Accept(tx, state, handlingOptions);

                if (!accepted)
                {
                    tx.ClearPreHash();
                    return accepted;
                }
            }

            filters = _postHashFilters;
            for (int i = 0; i < filters.Length; i++)
            {
                AcceptTxResult accepted = filters[i].Accept(tx, state, handlingOptions);

                if (!accepted) return accepted;
            }

            return AcceptTxResult.Accepted;
        }

        private AcceptTxResult AddCore(Transaction tx, TxFilteringState state, bool isPersistentBroadcast)
        {
            lock (_locker)
            {
                bool eip1559Enabled = _specProvider.GetCurrentHeadSpec().IsEip1559Enabled;
                UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(eip1559Enabled, _headInfo.CurrentBaseFee);
                TxDistinctSortedPool relevantPool = (tx.SupportsBlobs ? _blobTransactions : _transactions);

                relevantPool.TryGetBucketsWorstValue(tx.SenderAddress!, out Transaction? worstTx);
                tx.GasBottleneck = (worstTx is null || effectiveGasPrice <= worstTx.GasBottleneck)
                    ? effectiveGasPrice
                    : worstTx.GasBottleneck;

                bool inserted = relevantPool.TryInsert(tx.Hash!, tx, out Transaction? removed);

                if (!inserted)
                {
                    Metrics.PendingTransactionsPassedFiltersButCannotReplace++;
                    return AcceptTxResult.ReplacementNotAllowed;
                }

                if (tx.Hash == removed?.Hash)
                {
                    if (isPersistentBroadcast)
                    {
                        // it means it was added and immediately evicted - we are adding only to persistent broadcast
                        _broadcaster.Broadcast(tx, isPersistentBroadcast);
                    }
                    Metrics.PendingTransactionsPassedFiltersButCannotCompeteOnFees++;
                    return AcceptTxResult.FeeTooLowToCompete;
                }

                relevantPool.UpdateGroup(tx.SenderAddress!, state.SenderAccount, UpdateBucketWithAddedTransaction);
                Metrics.PendingTransactionsAdded++;
                if (tx.Supports1559) { Metrics.Pending1559TransactionsAdded++; }
                if (tx.SupportsBlobs) { Metrics.PendingBlobTransactionsAdded++; }

                if (removed is not null)
                {
                    EvictedPending?.Invoke(this, new TxEventArgs(removed));
                    // transaction which was on last position in sorted TxPool and was deleted to give
                    // a place for a newly added tx (with higher priority) is now removed from hashCache
                    // to give it opportunity to come back to TxPool in the future, when fees drops
                    _hashCache.DeleteFromLongTerm(removed.Hash!);
                    Metrics.PendingTransactionsEvicted++;
                }
            }

            _broadcaster.Broadcast(tx, isPersistentBroadcast);

            _hashCache.SetLongTerm(tx.Hash!);
            NewPending?.Invoke(this, new TxEventArgs(tx));
            Metrics.TransactionCount = _transactions.Count;
            Metrics.BlobTransactionCount = _blobTransactions.Count;
            return AcceptTxResult.Accepted;
        }

        private IEnumerable<(Transaction Tx, UInt256? changedGasBottleneck)> UpdateBucketWithAddedTransaction(
            Address address, Account account, EnhancedSortedSet<Transaction> transactions)
        {
            if (transactions.Count != 0)
            {
                UInt256 balance = account.Balance;
                long currentNonce = (long)(account.Nonce);

                foreach (var changedTx in UpdateGasBottleneck(transactions, currentNonce, balance))
                {
                    yield return changedTx;
                }
            }
        }

        private IEnumerable<(Transaction Tx, UInt256? changedGasBottleneck)> UpdateGasBottleneck(
            EnhancedSortedSet<Transaction> transactions, long currentNonce, UInt256 balance)
        {
            UInt256? previousTxBottleneck = null;
            int i = 0;
            UInt256 cumulativeCost = 0;

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
                    previousTxBottleneck ??= tx.CalculateAffordableGasPrice(_specProvider.GetCurrentHeadSpec().IsEip1559Enabled,
                            _headInfo.CurrentBaseFee, balance);

                    if (tx.MaxFeePerBlobGas < _headInfo.CurrentPricePerBlobGas)
                    {
                        gasBottleneck = UInt256.Zero;
                    }
                    else if (tx.Nonce == currentNonce + i)
                    {
                        UInt256 effectiveGasPrice =
                            tx.CalculateEffectiveGasPrice(_specProvider.GetCurrentHeadSpec().IsEip1559Enabled,
                                _headInfo.CurrentBaseFee);

                        if (tx.CheckForNotEnoughBalance(cumulativeCost, balance, out cumulativeCost))
                        {
                            // balance too low, remove tx from the pool
                            _broadcaster.StopBroadcast(tx.Hash!);
                            yield return (tx, null);
                        }
                        gasBottleneck = UInt256.Min(effectiveGasPrice, previousTxBottleneck ?? 0);
                    }

                    if (tx.GasBottleneck != gasBottleneck)
                    {
                        yield return (tx, gasBottleneck);
                    }

                    previousTxBottleneck = gasBottleneck;
                    i++;
                }
            }
        }

        private void UpdateBuckets()
        {
            lock (_locker)
            {
                _transactions.EnsureCapacity();
                _transactions.UpdatePool(_accounts, _updateBucket);

                _blobTransactions.EnsureCapacity();
                _blobTransactions.UpdatePool(_accounts, _updateBucket);
            }
        }

        private IEnumerable<(Transaction Tx, UInt256? changedGasBottleneck)> UpdateBucket(Address address, Account account, EnhancedSortedSet<Transaction> transactions)
        {
            if (transactions.Count != 0)
            {
                UInt256 balance = account.Balance;
                long currentNonce = (long)(account.Nonce);
                Transaction? tx = null;
                foreach (Transaction txn in transactions)
                {
                    if (txn.Nonce == currentNonce)
                    {
                        tx = txn;
                        break;
                    }
                }

                bool shouldBeDumped = false;

                if (tx is null)
                {
                    shouldBeDumped = true;
                }
                else if (balance < tx.Value)
                {
                    shouldBeDumped = true;
                }
                else if (!tx.Supports1559)
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
                hasBeenRemoved = _transactions.TryRemove(hash, out Transaction? transaction)
                                 || _blobTransactions.TryRemove(hash, out transaction);

                if (transaction is null || !hasBeenRemoved)
                    return false;
                if (hasBeenRemoved)
                {
                    RemovedPending?.Invoke(this, new TxEventArgs(transaction));
                }

                _broadcaster.StopBroadcast(hash);
            }

            if (_logger.IsTrace) _logger.Trace($"Removed a transaction: {hash}");

            return hasBeenRemoved;
        }

        public bool TryGetPendingTransaction(Keccak hash, out Transaction? transaction)
        {
            lock (_locker)
            {
                return _transactions.TryGetValue(hash, out transaction)
                       || _blobTransactions.TryGetValue(hash, out transaction)
                       || _broadcaster.TryGetPersistentTx(hash, out transaction);
            }
        }

        public bool TryGetPendingBlobTransaction(Keccak hash, [NotNullWhen(true)] out Transaction? blobTransaction)
        {
            lock (_locker)
            {
                return _blobTransactions.TryGetValue(hash, out blobTransaction);
            }
        }

        // only for tests - to test sorting
        internal void TryGetBlobTxSortingEquivalent(Keccak hash, out Transaction? transaction)
            => _blobTransactions.TryGetBlobTxSortingEquivalent(hash, out transaction);

        // should own transactions (in broadcaster) be also checked here?
        // maybe it should use NonceManager, as it already has info about local txs?
        public UInt256 GetLatestPendingNonce(Address address)
        {
            UInt256 maxPendingNonce = _accounts.GetAccount(address).Nonce;

            bool hasPendingTxs = _transactions.GetBucketCount(address) > 0;
            if (!hasPendingTxs && !(_blobTransactions.GetBucketCount(address) > 0))
            {
                // sender doesn't have txs in any pool, quick return
                return maxPendingNonce;
            }

            TxDistinctSortedPool relevantPool = (hasPendingTxs ? _transactions : _blobTransactions);
            // we are not doing any updating, but lets just use a thread-safe method without any data copying like snapshot
            relevantPool.UpdateGroup(address, (_, transactions) =>
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

        public bool IsKnown(Keccak? hash) => hash != null ? _hashCache.Get(hash) : false;

        public event EventHandler<TxEventArgs>? NewDiscovered;
        public event EventHandler<TxEventArgs>? NewPending;
        public event EventHandler<TxEventArgs>? RemovedPending;
        public event EventHandler<TxEventArgs>? EvictedPending;

        public void Dispose()
        {
            _timer?.Dispose();
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
                $"{(LruCache<ValueKeccak, object>.CalculateMemorySize(32, MemoryAllowance.TxHashCacheSize) + LruCache<Keccak, Transaction>.CalculateMemorySize(4096, MemoryAllowance.MemPoolSize)) / 1000 / 1000} MB"
                    .PadLeft(8));
        }

        private void TimerOnElapsed(object? sender, EventArgs e)
        {
            WriteTxPoolReport(_logger);

            _timer!.Enabled = true;
        }

        private static void WriteTxPoolReport(ILogger logger)
        {
            if (!logger.IsInfo)
            {
                return;
            }

            float preStateDiscards = (float)(Metrics.PendingTransactionsTooLowFee + Metrics.PendingTransactionsKnown + Metrics.PendingTransactionsGasLimitTooHigh) / Metrics.PendingTransactionsDiscarded;
            float receivedDiscarded = (float)Metrics.PendingTransactionsDiscarded / Metrics.PendingTransactionsReceived;

            // Set divisions by zero to 0
            if (float.IsNaN(preStateDiscards)) preStateDiscards = 0;
            if (float.IsNaN(receivedDiscarded)) receivedDiscarded = 0;

            logger.Info(@$"
Txn Pool State ({Metrics.TransactionCount:N0} txns queued)
Blob txs Pool ({Metrics.BlobTransactionCount:N0} txns queued)
------------------------------------------------
Sent
* Transactions:         {Metrics.PendingTransactionsSent,24:N0}
* Hashes:               {Metrics.PendingTransactionsHashesSent,24:N0}
------------------------------------------------
Total Received:         {Metrics.PendingTransactionsReceived,24:N0}
------------------------------------------------
Discarded at Filter Stage:
1.  GasLimitTooHigh:    {Metrics.PendingTransactionsGasLimitTooHigh,24:N0}
2.  Too Low Fee:        {Metrics.PendingTransactionsTooLowFee,24:N0}
3.  Malformed           {Metrics.PendingTransactionsMalformed,24:N0}
4.  Duplicate:          {Metrics.PendingTransactionsKnown,24:N0}
5.  Unknown Sender:     {Metrics.PendingTransactionsUnresolvableSender,24:N0}
6.  Conflicting TxType  {Metrics.PendingTransactionsConflictingTxType,24:N0}
7.  Zero Balance:       {Metrics.PendingTransactionsZeroBalance,24:N0}
8.  Balance < tx.value: {Metrics.PendingTransactionsBalanceBelowValue,24:N0}
9.  Nonce used:         {Metrics.PendingTransactionsLowNonce,24:N0}
10. Nonces skipped:     {Metrics.PendingTransactionsNonceGap,24:N0}
11. Balance Too Low:    {Metrics.PendingTransactionsTooLowBalance,24:N0}
12. Failed replacement  {Metrics.PendingTransactionsPassedFiltersButCannotReplace,24:N0}
13. Cannot Compete:     {Metrics.PendingTransactionsPassedFiltersButCannotCompeteOnFees,24:N0}
------------------------------------------------
Validated via State:    {Metrics.PendingTransactionsWithExpensiveFiltering,24:N0}
------------------------------------------------
Total Discarded:        {Metrics.PendingTransactionsDiscarded,24:N0}
------------------------------------------------
Discard Ratios:
* Pre-state Discards:   {preStateDiscards,24:P5}
* Received Discarded:   {receivedDiscarded,24:P5}
------------------------------------------------
Total Added:            {Metrics.PendingTransactionsAdded,24:N0}
* Eip1559 Added:        {Metrics.Pending1559TransactionsAdded,24:N0}
* Blob Added:        {Metrics.PendingBlobTransactionsAdded,24:N0}
------------------------------------------------
Total Evicted:          {Metrics.PendingTransactionsEvicted,24:N0}
------------------------------------------------
Ratios in last block:
* Eip1559 Transactions: {Metrics.Eip1559TransactionsRatio,24:P5}
* DarkPool Level1:      {Metrics.DarkPoolRatioLevel1,24:P5}
* DarkPool Level2:      {Metrics.DarkPoolRatioLevel2,24:P5}
Amounts:
* Blob txs:             {Metrics.BlobTransactionsInBlock,24:N0}
* Blobs:                {Metrics.BlobsInBlock,24:N0}
------------------------------------------------
");
        }
    }
}
