// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using static Nethermind.TxPool.Collections.TxDistinctSortedPool;
using ITimer = Nethermind.Core.Timers.ITimer;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.TxPool
{
    /// <summary>
    /// Stores all pending transactions. These will be used by block producer if this node is a miner / validator
    /// or simply for broadcasting and tracing in other cases.
    /// </summary>
    public class TxPool : ITxPool, IAsyncDisposable
    {
        private readonly RetryCache<PooledTransactionRequestMessage, ValueHash256> _retryCache;

        private readonly IIncomingTxFilter[] _preHashFilters;
        private readonly IIncomingTxFilter[] _postHashFilters;

        private readonly HashCache _hashCache = new();
        private readonly TxBroadcaster _broadcaster;

        private readonly TxDistinctSortedPool _transactions;
        private readonly BlobTxDistinctSortedPool _blobTransactions;

        private readonly IChainHeadSpecProvider _specProvider;
        private readonly IAccountStateProvider _accounts;
        private readonly AccountCache _accountCache;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly IBlobTxStorage _blobTxStorage;
        private readonly IChainHeadInfoProvider _headInfo;
        private readonly ITxPoolConfig _txPoolConfig;
        private readonly ITxValidator _specChangeTxValidator;
        private readonly string? _specChangeValidationFingerprint;
        private readonly ISpecChangeValidationStorage? _specChangeValidationStorage;
        private readonly bool _blobReorgsSupportEnabled;
        private readonly DelegationCache _pendingDelegations = new();
        private readonly HashSet<Hash256> _forkInvalidatedHashes = [];
        private IReleaseSpec? _forkInvalidatedSpec;

        private readonly ILogger _logger;

        private readonly Channel<HeadChange> _headBlocksChannel = Channel.CreateUnbounded<HeadChange>(new UnboundedChannelOptions() { SingleReader = true, SingleWriter = true });
        private readonly ReaderWriterLockSlim _newHeadLock = new(LockRecursionPolicy.SupportsRecursion);
        private readonly Lock _forkStateLock = new();
        private long _headGeneration;
        private long _forkStateVersion;

        private readonly UpdateGroupDelegate _updateBucket;
        private readonly UpdateGroupDelegate _updateBucketAdded;
        private readonly Task _headProcessing;
        private readonly CancellationTokenSource _cts;

        public event EventHandler<Block>? TxPoolHeadChanged;

        /// <summary>
        /// Indexes transactions
        /// </summary>
        private ulong _txIndex;

        private readonly ITimer? _timer;
        private ulong _lastBlockNumber = ulong.MaxValue;
        private Hash256? _lastBlockHash;

        /// <summary>
        /// The release specification every transaction currently in the pool has been validated against, or
        /// <see langword="null"/> when nothing is known, either because the pool has never been walked or
        /// because it took in a transaction judged by different rules.
        /// </summary>
        /// <remarks>
        /// Revalidation publishes a specification only after both pools have been walked. <see cref="AddCore"/>
        /// runs under the head read lock and may only clear the field. <see cref="_forkStateVersion"/> brackets
        /// both operations so block production can detect a concurrent state change without waiting for head
        /// processing.
        /// </remarks>
        private IReleaseSpec? _validatedSpec;

        private bool _isDisposed;
        private long _pendingTransactionsAdded = 0;

        /// <summary>
        /// This class stores all known pending transactions that can be used for block production
        /// (by miners or validators) or simply informing other nodes about known pending transactions (broadcasting).
        /// </summary>
        /// <param name="ecdsa">Used to recover sender addresses from transaction signatures.</param>
        /// <param name="blobTxStorage"></param>
        /// <param name="chainHeadInfoProvider"></param>
        /// <param name="txPoolConfig"></param>
        /// <param name="validator"></param>
        /// <param name="specChangeTxValidator">Validates transactions against the new fork rules, including light blob transactions.</param>
        /// <param name="logManager"></param>
        /// <param name="comparer"></param>
        /// <param name="transactionsGossipPolicy"></param>
        /// <param name="incomingTxFilters"></param>
        /// <param name="thereIsPriorityContract"></param>
        public TxPool(IEthereumEcdsa ecdsa,
            IBlobTxStorage blobTxStorage,
            IChainHeadInfoProvider chainHeadInfoProvider,
            ITxPoolConfig txPoolConfig,
            ITxValidator validator,
            [KeyFilter(ITxValidator.SpecChangeTxValidatorKey)] ITxValidator specChangeTxValidator,
            ILogManager? logManager,
            IComparer<Transaction> comparer,
            ITxGossipPolicy? transactionsGossipPolicy = null,
            IIncomingTxFilter[]? incomingTxFilters = null,
            bool thereIsPriorityContract = false)
        {
            _logger = logManager?.GetClassLogger<TxPool>() ?? throw new ArgumentNullException(nameof(logManager));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _blobTxStorage = blobTxStorage ?? throw new ArgumentNullException(nameof(blobTxStorage));
            _headInfo = chainHeadInfoProvider ?? throw new ArgumentNullException(nameof(chainHeadInfoProvider));
            _txPoolConfig = txPoolConfig;
            _specChangeTxValidator = specChangeTxValidator ?? throw new ArgumentNullException(nameof(specChangeTxValidator));
            _specChangeValidationFingerprint = (specChangeTxValidator as ISpecChangeTxValidator)?.PersistenceFingerprint;
            _specChangeValidationStorage = txPoolConfig.BlobsSupport.IsPersistentStorage()
                && _specChangeValidationFingerprint is not null
                ? blobTxStorage as ISpecChangeValidationStorage
                : null;
            AcceptTxWhenNotSynced = txPoolConfig.AcceptTxWhenNotSynced;
            _blobReorgsSupportEnabled = txPoolConfig.BlobsSupport.SupportsReorgs();
            _accounts = _accountCache = new AccountCache(_headInfo.ReadOnlyStateProvider);
            _specProvider = _headInfo.SpecProvider;
            SupportsBlobs = _txPoolConfig.BlobsSupport != BlobsSupportMode.Disabled;
            _cts = new();
            _retryCache = new RetryCache<PooledTransactionRequestMessage, ValueHash256>(
                logManager,
                TimeProvider.System,
                requestingCacheSize: MemoryAllowance.TxHashCacheSize / 10,
                token: _cts.Token,
                overflowRequestLimit: RetryCache<PooledTransactionRequestMessage, ValueHash256>.DefaultOverflowRequestLimit);

            MemoryAllowance.MemPoolSize = txPoolConfig.Size;

            // Capture closures once rather than per invocation
            _updateBucket = UpdateBucket;
            _updateBucketAdded = UpdateBucketWithAddedTransaction;

            _broadcaster = new TxBroadcaster(comparer, TimerFactory.Default, txPoolConfig, chainHeadInfoProvider, logManager, transactionsGossipPolicy);
            TxPoolHeadChanged += _broadcaster.OnNewHead;

            _transactions = new TxDistinctSortedPool(txPoolConfig.Size, comparer, logManager);
            _transactions.Inserted += OnInsertedTx;
            _transactions.Removed += OnRemovedTx;

            _blobTransactions = txPoolConfig.BlobsSupport.IsPersistentStorage()
                ? new PersistentBlobTxDistinctSortedPool(blobTxStorage, _txPoolConfig, comparer, logManager)
                : new BlobTxDistinctSortedPool(txPoolConfig.BlobsSupport == BlobsSupportMode.InMemory ? _txPoolConfig.InMemoryBlobPoolSize : 0, comparer, logManager);
            UpdateBucketsWithoutRevalidation();
            InitializeValidatedSpec();

            _headInfo.HeadChanged += OnHeadChange;

            _preHashFilters =
            [
                new NotSupportedTxFilter(txPoolConfig, _logger),
                new SizeTxFilter(txPoolConfig, _logger),
                new GasLimitTxFilter(_headInfo, txPoolConfig, logManager),
                new PriorityFeeTooLowFilter(_headInfo, txPoolConfig, _logger),
                new FeeTooLowFilter(_headInfo, _transactions, _blobTransactions, thereIsPriorityContract, _logger)
            ];

            List<IIncomingTxFilter> postHashFilters =
            [
                new NullHashTxFilter(), // needs to be first as it assigns the hash
                new AlreadyKnownTxFilter(_hashCache, _logger),
                new MalformedTxFilter(validator, _specChangeTxValidator, ecdsa, _logger),
                new TxTypeTxFilter(_transactions,
                    _blobTransactions), // has to be after MalformedTxFilter as it uses the recovered sender
                new BalanceZeroFilter(thereIsPriorityContract, _logger),
                new BalanceTooLowFilter(_transactions, _blobTransactions, _logger),
                new LowNonceFilter(_logger), // has to be after MalformedTxFilter as it uses the recovered sender
                new FutureNonceFilter(txPoolConfig),
                new GapNonceFilter(_transactions, _blobTransactions, _logger),
                new RecoverAuthorityFilter(ecdsa),
                new DelegatedAccountFilter(_transactions, _blobTransactions, chainHeadInfoProvider.ReadOnlyStateProvider, _pendingDelegations),
            ];

            if (incomingTxFilters is not null)
            {
                postHashFilters.AddRange(incomingTxFilters);
            }

            postHashFilters.Add(new DeployedCodeFilter(chainHeadInfoProvider.ReadOnlyStateProvider));

            _postHashFilters = postHashFilters.ToArray();

            int? reportMinutes = txPoolConfig.ReportMinutes;
            if (_logger.IsInfo && reportMinutes.HasValue)
            {
                _timer = TimerFactory.Default.CreateTimer(TimeSpan.FromMinutes(reportMinutes.Value));
                _timer.AutoReset = false;
                _timer.Elapsed += TimerOnElapsed;
                _timer.Start();
            }

            _headProcessing = ProcessNewHeads();
        }

        public Transaction[] GetPendingTransactions() => _transactions.GetSnapshot();

        public int GetPendingTransactionsCount() => _transactions.Count;

        public IDictionary<AddressAsKey, Transaction[]> GetPendingTransactionsBySender(bool filterToReadyTx = false, UInt256 baseFee = default) =>
            _transactions.GetBucketSnapshot(filterToReadyTx ?
                (data => data.first.CanPayBaseFee(baseFee) && data.first.Nonce == _accounts.GetNonce(data.key)) :
                null);

        public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender() =>
            _blobTransactions.GetBucketSnapshot();

        public Transaction[] GetPendingTransactionsBySender(Address address) =>
            _transactions.GetBucketSnapshot(address);

        public Transaction[] GetPendingLightBlobTransactionsBySender(Address address) =>
            _blobTransactions.GetBucketSnapshot(address);

        // only for testing reasons
        internal Transaction[] GetOwnPendingTransactions() => _broadcaster.GetSnapshot();

        public int GetPendingBlobTransactionsCount() => _blobTransactions.Count;



        public bool TryGetBlobAndProofV0(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[]? proof)
            => _blobTransactions.TryGetBlobAndProofV0(blobVersionedHash, out blob, out proof);

        public bool TryGetBlobAndProofV1(byte[] blobVersionedHash,
            [NotNullWhen(true)] out byte[]? blob,
            [NotNullWhen(true)] out byte[][]? cellProofs)
            => _blobTransactions.TryGetBlobAndProofV1(blobVersionedHash, out blob, out cellProofs);

        public int TryGetBlobsAndProofsV1(byte[][] requestedBlobVersionedHashes,
            Span<byte[]?> blobs, Span<ReadOnlyMemory<byte[]>> proofs)
            => _blobTransactions.TryGetBlobsAndProofsV1(requestedBlobVersionedHashes, blobs, proofs);

        private void OnInsertedTx(object? sender, SortedPool<ValueHash256, Transaction, AddressAsKey>.SortedPoolEventArgs args) => AddPendingDelegations(args.Value);
        private void OnRemovedTx(object? sender, SortedPool<ValueHash256, Transaction, AddressAsKey>.SortedPoolRemovedEventArgs args) => RemovePendingDelegations(args.Value);
        private void OnHeadChange(object? sender, BlockReplacementEventArgs e)
        {
            if (_headInfo.IsSyncing)
            {
                DisposeBlockAccountChanges(e.Block);
                return;
            }

            try
            {
                long generation = Interlocked.Increment(ref _headGeneration);
                _headBlocksChannel.Writer.TryWrite(new HeadChange(e, generation));
            }
            catch (Exception exception)
            {
                if (_logger.IsError)
                    _logger.Error(
                        $"Couldn't correctly add or remove transactions from txpool after processing block {e.Block!.ToString(Block.Format.FullHashAndNumber)}.", exception);
            }
        }

        private async Task ProcessNewHeads()
        {
            try
            {
                await Task.Run(ProcessNewHeadLoop);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"TxPool update after block queue failed.", ex);
            }
        }

        private async Task ProcessNewHeadLoop()
        {
            TryRevalidateCurrentSpec(Volatile.Read(ref _headGeneration));

            while (await _headBlocksChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_headBlocksChannel.Reader.TryRead(out HeadChange headChange))
                {
                    BlockReplacementEventArgs args = headChange.Args;
                    try
                    {
                        _newHeadLock.EnterWriteLock();
                        try
                        {
                            ArrayPoolList<AddressAsKey>? accountChanges = args.Block.AccountChanges;
                            if (args.PreviousBlock is not null || !CanUseCache(args.Block, accountChanges))
                            {
                                // Non-sequential block or reorganization detected, reset cache
                                _accountCache.Reset();
                            }
                            else
                            {
                                // Sequential block, just remove changed accounts from cache
                                _accountCache.RemoveAccounts(accountChanges);
                            }

                            DisposeBlockAccountChanges(args.Block);

                            _lastBlockNumber = args.Block.Number;
                            _lastBlockHash = args.Block.Hash;

                            ReAddReorganisedTransactions(args.PreviousBlock);
                            RemoveProcessedTransactions(args.Block);

                            if (!_headInfo.IsSyncing || AcceptTxWhenNotSynced || args.PreviousBlock is not null)
                            {
                                _hashCache.ClearCurrentBlockCache();
                            }

                            UpdateBucketsWithoutRevalidation();
                        }
                        finally
                        {
                            _newHeadLock.ExitWriteLock();
                        }

                        TryRevalidateCurrentSpec(headChange.Generation);
                        TxPoolHeadChanged?.Invoke(this, args.Block);
                        Metrics.TransactionCount = _transactions.Count;
                        Metrics.BlobTransactionCount = _blobTransactions.Count;
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsWarn) _logger.Warn($"TxPool failed to update after block {args.Block.ToString(Block.Format.FullHashAndNumber)} with exception {e}");
                    }
                }
            }

            bool CanUseCache(Block block, [NotNullWhen(true)] ArrayPoolList<AddressAsKey>? accountChanges) => accountChanges is not null && block.ParentHash == _lastBlockHash && _lastBlockNumber + 1 == block.Number;
        }

        private void ReAddReorganisedTransactions(Block? previousBlock)
        {
            if (previousBlock is not null)
            {
                Metrics.TransactionsReorged += previousBlock.Transactions.Length;
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

                if (_blobReorgsSupportEnabled
                    && _blobTxStorage.TryGetBlobTransactionsFromBlock(previousBlock.Number, out Transaction[]? blobTxs)
                    && blobTxs is not null)
                {
                    foreach (Transaction blobTx in blobTxs)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Readded tx {blobTx.Hash} from reorged block {previousBlock.Number} (hash {previousBlock.Hash}) to blob pool");
                        _hashCache.Delete(blobTx.Hash!);
                        if (blobTx.SenderAddress is null)
                        {
                            if (!_ecdsa.TryRecoverAddress(blobTx, out Address? senderAddress))
                            {
                                RecordUnrecoverableReorgedBlobTx(blobTx, previousBlock);
                                continue;
                            }

                            blobTx.SenderAddress = senderAddress;
                        }
                        SubmitTx(blobTx, isEip155Enabled ? TxHandlingOptions.None : TxHandlingOptions.PreEip155Signing);
                    }
                    if (_logger.IsTrace) _logger.Trace($"Readded txs from reorged block {previousBlock.Number} (hash {previousBlock.Hash}) to blob pool");

                    _blobTxStorage.DeleteBlobTransactionsFromBlock(previousBlock.Number);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RecordUnrecoverableReorgedBlobTx(Transaction blobTx, Block previousBlock)
        {
            Metrics.PendingTransactionsUnresolvableSender++;
            if (_logger.IsDebug) _logger.Debug($"Skipped readding tx {blobTx.Hash} from reorged block {previousBlock.Number} (hash {previousBlock.Hash}) to blob pool: sender address is not recoverable");
        }

        private void RemoveProcessedTransactions(Block block)
        {
            Transaction[] blockTransactions = block.Transactions;
            using ArrayPoolListRef<Transaction> blobTxsToSave = new((int)_specProvider.GetSpec(block.Header).MaxBlobCount);
            long discoveredForPendingTxs = 0;
            long discoveredForHashCache = 0;
            long notInMempool = 0;
            long eip1559Txs = 0;
            long eip7702Txs = 0;
            long blobTxs = 0;
            long blobs = 0;

            for (int i = 0; i < blockTransactions.Length; i++)
            {
                Transaction blockTx = blockTransactions[i];
                Hash256 txHash = blockTx.Hash ?? throw new ArgumentException("Hash was unexpectedly null!");

                if (blockTx.Supports1559)
                {
                    eip1559Txs++;
                }

                if (blockTx.SupportsAuthorizationList)
                {
                    eip7702Txs++;
                }

                if (blockTx.SupportsBlobs)
                {
                    blobTxs++;
                    blobs += (long)blockTx.GetBlobCount();

                    if (_blobReorgsSupportEnabled)
                    {
                        if (_blobTransactions.TryGetValue(blockTx.Hash, out Transaction? fullBlobTx))
                        {
                            if (_logger.IsTrace) _logger.Trace($"Saved processed blob tx {blockTx.Hash} from block {block.Number} to ProcessedTxs db");
                            blobTxsToSave.Add(fullBlobTx);
                        }
                        else if (_logger.IsTrace) _logger.Trace($"Skipped adding processed blob tx {blockTx.Hash} from block {block.Number} to ProcessedTxs db - not found in blob pool");
                    }
                }

                bool isKnown = IsKnown(txHash);
                if (!isKnown)
                {
                    discoveredForHashCache++;
                }

                bool isPending = RemoveIncludedTransaction(blockTx);
                if (!isPending)
                {
                    discoveredForPendingTxs++;
                }

                if (!isKnown && !isPending)
                {
                    notInMempool++;
                }
            }

            if (blobTxsToSave.Count > 0)
            {
                _blobTxStorage.AddBlobTransactionsFromBlock(block.Number, blobTxsToSave);
            }

            long transactionsInBlock = blockTransactions.Length;
            if (transactionsInBlock != 0)
            {
                Metrics.DarkPoolRatioLevel1 = (float)discoveredForHashCache / transactionsInBlock;
                Metrics.DarkPoolRatioLevel2 = (float)discoveredForPendingTxs / transactionsInBlock;
                Metrics.Eip1559TransactionsRatio = (float)eip1559Txs / transactionsInBlock;
                Metrics.Eip7702TransactionsInBlock = eip7702Txs;
                Metrics.BlobTransactionsInBlock = blobTxs;
                Metrics.BlobsInBlock = blobs;
                Metrics.TransactionsSourcedPrivateOrderFlow += notInMempool;
                Metrics.TransactionsSourcedMemPool += transactionsInBlock - notInMempool;
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
                if (_logger.IsTrace) _logger.Trace($"Added a peer to TX pool: {peer}");

                // Announce txs to newly connected peer only if we are synced. If chain head of the peer is higher by
                // more than 16 blocks than our head, skip announcing txs as some of them are probably already processed
                // Also skip announcing if peer's head number is shown as 0 as then we don't know peer's head block yet
                if (peer.HeadNumber != 0 && peer.HeadNumber < _headInfo.HeadNumber + 16)
                {
                    Transaction[] txSnapshot = _transactions.GetSnapshot();
                    Transaction[] blobTxSnapshot = _blobTransactions.GetSnapshot();
                    _broadcaster.AnnounceOnce(peer, txSnapshot);
                    _broadcaster.AnnounceOnce(peer, blobTxSnapshot);
                    if (_logger.IsTrace) _logger.Trace($"Announced {txSnapshot.Length} txs and {blobTxSnapshot.Length} blob txs to peer {peer}");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Skipped announcing txs to peer {peer} because of syncing. Peer is on head {peer.HeadNumber}, we are at {_headInfo.HeadNumber}");
                }
            }
        }

        public void RemovePeer(PublicKey nodeId)
        {
            if (_broadcaster.RemovePeer(nodeId))
            {
                if (_logger.IsTrace) _logger.Trace($"Removed a peer from TX pool: {nodeId}");
            }
        }

        public bool AcceptTxWhenNotSynced { get; set; }
        public bool SupportsBlobs { get; }
        public long PendingTransactionsAdded => Volatile.Read(ref _pendingTransactionsAdded);

        /// This is a debug/testing method that clears the entire txpool state.
        /// Currently only used in the Taiko integration tests after chain reorgs.
        public void ResetTxPoolState()
        {
            _newHeadLock.EnterWriteLock();
            try
            {
                // Clear hash cache and account cache
                _hashCache.ClearAll();
                _accountCache.Reset();

                // Also clear all pending transactions
                // Get snapshot first to avoid modifying collection while iterating
                Transaction[] pendingTxs = _transactions.GetSnapshot();
                foreach (Transaction tx in pendingTxs)
                {
                    RemoveTransaction(tx.Hash);
                }

                // Clear blob transactions too
                Transaction[] pendingBlobTxs = _blobTransactions.GetSnapshot();
                foreach (Transaction tx in pendingBlobTxs)
                {
                    RemoveTransaction(tx.Hash);
                }

                // Update metrics after removal
                Metrics.TransactionCount = _transactions.Count;
                Metrics.BlobTransactionCount = _blobTransactions.Count;
            }
            finally
            {
                _newHeadLock.ExitWriteLock();
            }
        }

        public AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions handlingOptions)
        {
            bool startBroadcast = _txPoolConfig.PersistentBroadcastEnabled
                                  && (handlingOptions & TxHandlingOptions.PersistentBroadcast) ==
                                  TxHandlingOptions.PersistentBroadcast;

            if (!AcceptTxWhenNotSynced &&
                _headInfo.IsSyncing &&
                // If local tx allow it to be accepted even when syncing
                !startBroadcast)
            {
                return AcceptTxResult.Syncing;
            }

            Metrics.PendingTransactionsReceived++;

            // assign a sequence number to transaction so we can order them by arrival times when
            // gas prices are exactly the same
            tx.PoolIndex = Interlocked.Increment(ref _txIndex);

            NewDiscovered?.Invoke(this, new TxEventArgs(tx));

            if (_logger.IsTrace)
            {
                TraceTx(tx, handlingOptions, startBroadcast);
            }

            if (_txPoolConfig.ProofsTranslationEnabled
                && !BlobProofsTranslator.TryTranslateToCurrentProofVersion(tx, _headInfo.CurrentProofVersion))
            {
                Metrics.PendingTransactionsDiscarded++;
                return AcceptTxResult.Invalid;
            }

            AcceptTxResult accepted = AcceptTxResult.Invalid;

            _newHeadLock.EnterReadLock();
            try
            {
                TxFilteringState state = new(tx, _accounts, _specProvider.GetCurrentHeadSpec());
                accepted = FilterTransactions(tx, handlingOptions, ref state);
                if (accepted)
                {
                    accepted = AddCore(tx, ref state, startBroadcast);
                }
                else
                {
                    Metrics.PendingTransactionsDiscarded++;
                }
            }
            finally
            {
                _newHeadLock.ExitReadLock();
            }

            if (accepted != AcceptTxResult.Invalid)
            {
                _retryCache.Received(tx.Hash!);
            }

            return accepted;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceTx(Transaction tx, TxHandlingOptions handlingOptions, bool startBroadcast)
            {
                bool managedNonce = (handlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
                _logger.Trace($"Adding transaction {tx.ToString("  ")} - managed nonce: {managedNonce} | persistent broadcast {startBroadcast}");
            }
        }

        public AnnounceResult NotifyAboutTx(Hash256 hash, IMessageHandler<PooledTransactionRequestMessage> retryHandler) =>
            (!AcceptTxWhenNotSynced && _headInfo.IsSyncing) || _hashCache.Get(hash) ?
                AnnounceResult.Delayed :
                _retryCache.Announced(hash, retryHandler);

        private AcceptTxResult FilterTransactions(Transaction tx, TxHandlingOptions handlingOptions, ref TxFilteringState state)
        {
            IIncomingTxFilter[] filters = _preHashFilters;
            for (int i = 0; i < filters.Length; i++)
            {
                AcceptTxResult accepted = filters[i].Accept(tx, ref state, handlingOptions);

                if (!accepted)
                {
                    tx.ClearPreHash();
                    return accepted;
                }
            }

            filters = _postHashFilters;
            for (int i = 0; i < filters.Length; i++)
            {
                AcceptTxResult accepted = filters[i].Accept(tx, ref state, handlingOptions);

                if (!accepted) return accepted;
            }

            return AcceptTxResult.Accepted;
        }

        private AcceptTxResult AddCore(Transaction tx, ref TxFilteringState state, bool isPersistentBroadcast)
        {
            IReleaseSpec headSpec = state.HeadSpec;

            // The filters judged this transaction by headSpec. If the pool is marked as validated against a
            // different specification that mark stops covering the pool here, so drop it before the
            // transaction becomes visible to IsRevalidatedFor.
            if (Volatile.Read(ref _validatedSpec) is IReleaseSpec validatedSpec && !ReferenceEquals(validatedSpec, headSpec))
            {
                InvalidateValidatedSpec();
            }

            bool eip1559Enabled = headSpec.IsEip1559Enabled;
            UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(eip1559Enabled, _headInfo.CurrentBaseFee);
            TxDistinctSortedPool relevantPool = (tx.SupportsBlobs ? _blobTransactions : _transactions);

            relevantPool.TryGetBucketsWorstValue(tx.SenderAddress!, out Transaction? worstTx);
            tx.GasBottleneck = (worstTx is null || effectiveGasPrice <= worstTx.GasBottleneck)
                ? effectiveGasPrice
                : worstTx.GasBottleneck;

            bool inserted = relevantPool.TryInsert(tx.Hash!, tx, out Transaction? removed);

            if (!inserted)
            {
                // it means it failed on adding to the pool - it is possible when new tx has the same sender
                // and nonce as already existent tx and is not good enough to replace it
                Metrics.PendingTransactionsPassedFiltersButCannotReplace++;
                return AcceptTxResult.ReplacementNotAllowed;
            }

            if (tx.Hash == removed?.Hash)
            {
                // it means it was added and immediately evicted - pool was full of better txs
                if (!isPersistentBroadcast || tx.SupportsBlobs || !_broadcaster.Broadcast(tx, true))
                {
                    // we are adding only to persistent broadcast - not good enough for standard pool,
                    // but can be good enough for TxBroadcaster pool - for local txs only
                    Metrics.PendingTransactionsPassedFiltersButCannotCompeteOnFees++;
                    return AcceptTxResult.FeeTooLowToCompete;
                }
                else
                {
                    return AcceptTxResult.Accepted;
                }
            }

            relevantPool.UpdateGroup(tx.SenderAddress!, state.SenderAccount, _updateBucketAdded);
            Interlocked.Increment(ref Metrics.PendingTransactionsAdded);
            Interlocked.Increment(ref _pendingTransactionsAdded);
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

            _broadcaster.Broadcast(tx, isPersistentBroadcast);

            _hashCache.SetLongTerm(tx.Hash!);
            NewPending?.Invoke(this, new TxEventArgs(tx));
            Metrics.TransactionCount = _transactions.Count;
            Metrics.BlobTransactionCount = _blobTransactions.Count;
            return AcceptTxResult.Accepted;
        }

        private void AddPendingDelegations(Transaction tx)
        {
            if (tx.HasAuthorizationList)
            {
                foreach (AuthorizationTuple auth in tx.AuthorizationList)
                {
                    if (auth.Authority is not null)
                        _pendingDelegations.IncrementDelegationCount(auth.Authority!);
                }
            }
        }

        private void RemovePendingDelegations(Transaction transaction)
        {
            if (transaction.HasAuthorizationList)
            {
                foreach (AuthorizationTuple auth in transaction.AuthorizationList)
                {
                    if (auth.Authority is not null)
                        _pendingDelegations.DecrementDelegationCount(auth.Authority!);
                }
            }
        }

        private void UpdateBucketWithAddedTransaction(in AccountStruct account, EnhancedSortedSet<Transaction> transactions, ref Transaction? lastElement, UpdateTransactionDelegate updateTx)
        {
            if (transactions.Count != 0)
            {
                UInt256 balance = account.Balance;
                ulong currentNonce = account.Nonce;

                UpdateGasBottleneckAndMarkForEviction(transactions, currentNonce, balance, lastElement, updateTx, revalidation: null);
            }
        }

        /// <returns>How many transactions were dropped as invalid under <paramref name="revalidation"/>.</returns>
        private int UpdateGasBottleneckAndMarkForEviction(
            EnhancedSortedSet<Transaction> transactions,
            ulong currentNonce,
            UInt256 balance,
            Transaction? lastElement,
            UpdateTransactionDelegate updateTx,
            ForkRevalidation? revalidation)
        {
            UInt256? previousTxBottleneck = null;
            int i = 0;
            UInt256 cumulativeCost = 0;
            IReleaseSpec headSpec = _specProvider.GetCurrentHeadSpec();
            bool isEip1559 = headSpec.IsEip1559Enabled;
            bool evictNextTxs = false;
            int invalidatedByFork = 0;

            foreach (Transaction tx in transactions)
            {
                if (tx.Nonce < currentNonce)
                {
                    MarkForEviction(tx, false);
                    continue;
                }

                try
                {
                    UInt256 gasBottleneck = 0;

                    if (revalidation is not null)
                    {
                        ForkValidationResult validation = revalidation.Validate(tx);

                        if (!validation.Validation)
                        {
                            invalidatedByFork++;
                            MarkForEviction(tx, revalidation.RecordEviction(tx, validation), evictFollowingTransactions: true);
                            continue;
                        }
                    }

                    previousTxBottleneck ??= tx.CalculateAffordableGasPrice(
                        isEip1559,
                        _headInfo.CurrentBaseFee, balance);

                    // it is not affecting non-blob txs - for them MaxFeePerBlobGas is null, so check is skipped
                    if (tx.MaxFeePerBlobGas < _headInfo.CurrentFeePerBlobGas)
                    {
                        gasBottleneck = UInt256.Zero;
                    }
                    else if (tx.Nonce == currentNonce + (ulong)i)
                    {
                        UInt256 effectiveGasPrice =
                            tx.CalculateEffectiveGasPrice(isEip1559,
                                _headInfo.CurrentBaseFee);

                        if (tx.CheckForNotEnoughBalance(cumulativeCost, balance, out cumulativeCost))
                        {
                            // balance too low, remove tx from the pool
                            MarkForEviction(tx, false);
                        }

                        gasBottleneck = UInt256.Min(effectiveGasPrice, previousTxBottleneck ?? 0);
                    }

                    if (tx.GasBottleneck != gasBottleneck)
                    {
                        updateTx(transactions, tx, gasBottleneck, lastElement);
                    }

                    previousTxBottleneck = gasBottleneck;

                    if (evictNextTxs)
                    {
                        MarkForEviction(tx, true);
                    }
                }
                finally
                {
                    i++;
                }
            }

            return invalidatedByFork;

            void MarkForEviction(Transaction tx, bool allowLaterPoolReentrance, bool evictFollowingTransactions = false)
            {
                _broadcaster.StopBroadcast(tx.Hash!);
                if (allowLaterPoolReentrance) _hashCache.DeleteFromLongTerm(tx.Hash!);
                updateTx(transactions, tx, null, lastElement);
                // evict all following txs to prevent nonce gaps between blob tx
                evictNextTxs |= tx.SupportsBlobs || evictFollowingTransactions;
            }
        }

        private void UpdateBucketsWithoutRevalidation()
        {
            _transactions.UpdatePool(_accounts, _updateBucket);
            _blobTransactions.UpdatePool(_accounts, _updateBucket);
        }

        private void InitializeValidatedSpec()
        {
            IReleaseSpec headSpec = _specProvider.GetCurrentHeadSpec();
            string expectedMarker = GetSpecChangeValidationMarker(headSpec);
            bool isEmpty = _transactions.Count == 0 && _blobTransactions.Count == 0;
            bool markerMatches = _specChangeValidationStorage?.GetSpecChangeValidationMarker() == expectedMarker;

            if (isEmpty || markerMatches)
            {
                PublishValidatedSpec(headSpec);
            }
            else
            {
                _specChangeValidationStorage?.SetSpecChangeValidationMarker(null);
            }
        }

        private void TryRevalidateCurrentSpec(long generation)
        {
            try
            {
                TryRevalidateCurrentSpecCore(generation);
            }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Warn($"TxPool failed to revalidate transactions after a protocol change with exception {exception}");
            }
        }

        private void TryRevalidateCurrentSpecCore(long generation)
        {
            IReleaseSpec spec;

            _newHeadLock.EnterWriteLock();
            try
            {
                if (!IsRevalidationGenerationCurrent(generation))
                {
                    return;
                }

                spec = _specProvider.GetCurrentHeadSpec();
                if (ReferenceEquals(Volatile.Read(ref _validatedSpec), spec))
                {
                    return;
                }

                ReleaseForkInvalidatedHashesFor(spec);
                InvalidateValidatedSpec();

                if (_transactions.Count == 0 && _blobTransactions.Count == 0)
                {
                    PublishValidatedSpec(spec);
                    return;
                }
            }
            finally
            {
                _newHeadLock.ExitWriteLock();
            }

            ForkRevalidation revalidation = new(this, spec, generation);
            if (!revalidation.IsComplete)
            {
                return;
            }

            _newHeadLock.EnterWriteLock();
            try
            {
                if (!CanApplyRevalidation(spec, generation))
                {
                    return;
                }

                _transactions.UpdatePool(_accounts, revalidation.UpdateBucket);
                _blobTransactions.UpdatePool(_accounts, revalidation.UpdateBlobBucket);

                if (!CanApplyRevalidation(spec, generation))
                {
                    InvalidateValidatedSpec();
                    return;
                }

                PublishValidatedSpec(spec);

                if (revalidation.RemovedCount != 0)
                {
                    Metrics.PendingTransactionsEvicted += revalidation.RemovedCount;
                    if (_logger.IsInfo) _logger.Info($"Removed {revalidation.RemovedCount:N0} transactions invalid under {spec.Name} after the protocol change.");
                }
            }
            finally
            {
                _newHeadLock.ExitWriteLock();
            }
        }

        private bool IsRevalidationGenerationCurrent(long generation) =>
            !_cts.IsCancellationRequested && generation == Volatile.Read(ref _headGeneration);

        private bool CanApplyRevalidation(IReleaseSpec spec, long generation) =>
            IsRevalidationGenerationCurrent(generation) && ReferenceEquals(spec, _specProvider.GetCurrentHeadSpec());

        private void ReleaseForkInvalidatedHashesFor(IReleaseSpec spec)
        {
            if (_forkInvalidatedSpec is null || ReferenceEquals(_forkInvalidatedSpec, spec))
            {
                return;
            }

            foreach (Hash256 hash in _forkInvalidatedHashes)
            {
                _hashCache.DeleteFromLongTerm(hash);
            }

            _forkInvalidatedHashes.Clear();
            _forkInvalidatedSpec = null;
        }

        private void RememberForkInvalidatedHash(Hash256 hash, IReleaseSpec spec)
        {
            _forkInvalidatedSpec = spec;
            _forkInvalidatedHashes.Add(hash);
        }

        private void PublishValidatedSpec(IReleaseSpec spec)
        {
            lock (_forkStateLock)
            {
                Interlocked.Increment(ref _forkStateVersion);
                try
                {
                    _specChangeValidationStorage?.SetSpecChangeValidationMarker(GetSpecChangeValidationMarker(spec));
                    Volatile.Write(ref _validatedSpec, spec);
                }
                finally
                {
                    Interlocked.Increment(ref _forkStateVersion);
                }
            }
        }

        private void InvalidateValidatedSpec()
        {
            lock (_forkStateLock)
            {
                if (Volatile.Read(ref _validatedSpec) is null)
                {
                    return;
                }

                Interlocked.Increment(ref _forkStateVersion);
                try
                {
                    Volatile.Write(ref _validatedSpec, null);
                    _specChangeValidationStorage?.SetSpecChangeValidationMarker(null);
                }
                finally
                {
                    Interlocked.Increment(ref _forkStateVersion);
                }
            }
        }

        private string GetSpecChangeValidationMarker(IReleaseSpec spec)
        {
            SpecGasCosts gasCosts = spec.GasCosts;
            return FormattableString.Invariant($"1|{ProductInfo.Version}|{ProductInfo.Commit}|{_specChangeValidationFingerprint}")
                + FormattableString.Invariant($"|{spec.IsEip2Enabled}|{spec.IsEip155Enabled}|{spec.ValidateChainId}|{spec.IsEip2028Enabled}")
                + FormattableString.Invariant($"|{spec.IsEip2780Enabled}|{spec.IsEip2930Enabled}|{spec.MaxInitCodeSize}")
                + FormattableString.Invariant($"|{spec.IsEip1559Enabled}|{spec.IsEip3860Enabled}|{spec.IsEip4844Enabled}|{spec.IsEip7623Enabled}")
                + FormattableString.Invariant($"|{spec.IsEip7702Enabled}|{spec.IsEip7976Enabled}|{spec.IsEip7981Enabled}|{spec.IsEip8037Enabled}|{spec.IsEip8038Enabled}")
                + FormattableString.Invariant($"|{gasCosts.TxDataNonZeroMultiplier}|{gasCosts.TotalCostFloorPerToken}|{gasCosts.MaxBlobGasPerBlock}|{gasCosts.MaxBlobGasPerTx}")
                + FormattableString.Invariant($"|{spec.GetTxGasLimitCap()}|{spec.BlobProofVersion}");
        }

        /// <summary>
        /// One pass of pool revalidation against a newly activated release specification.
        /// </summary>
        /// <remarks>
        /// Validation is collected outside the head lock. Persistent blob bodies are read directly from storage
        /// in bounded batches, avoiding both the blob-pool lock and pollution of its sidecar cache. Only the
        /// short removal pass runs under the head write lock.
        /// </remarks>
        private sealed class ForkRevalidation
        {
            private const int BlobReadBatchSize = 16;

            private readonly TxPool _pool;
            private readonly IReleaseSpec _spec;
            private readonly Dictionary<Hash256, ForkValidationResult> _invalidTransactions = [];

            public ForkRevalidation(TxPool pool, IReleaseSpec spec, long generation)
            {
                _pool = pool;
                _spec = spec;
                IsComplete = FindInvalidTransactions(generation);
            }

            public bool IsComplete { get; }

            /// <summary>How many transactions the pass has dropped as invalid under the new specification.</summary>
            public int RemovedCount { get; private set; }

            public void UpdateBucket(in AccountStruct account, EnhancedSortedSet<Transaction> transactions, ref Transaction? lastElement, UpdateTransactionDelegate updateTx) =>
                RemovedCount += _pool.UpdateBucketCore(account, transactions, ref lastElement, updateTx, this);

            public void UpdateBlobBucket(in AccountStruct account, EnhancedSortedSet<Transaction> transactions, ref Transaction? lastElement, UpdateTransactionDelegate updateTx) =>
                RemovedCount += _pool.UpdateBucketCore(account, transactions, ref lastElement, updateTx, this);

            public ForkValidationResult Validate(Transaction transaction) =>
                _invalidTransactions.GetValueOrDefault(transaction.Hash!);

            public bool RecordEviction(Transaction transaction, in ForkValidationResult validation)
            {
                if (validation.AllowImmediateResubmission)
                {
                    return true;
                }

                if (validation.AllowAfterSpecChange)
                {
                    _pool.RememberForkInvalidatedHash(transaction.Hash!, _spec);
                }

                return false;
            }

            private bool FindInvalidTransactions(long generation)
            {
                Transaction[] transactions = _pool._transactions.GetSnapshot();
                for (int i = 0; i < transactions.Length; i++)
                {
                    if (!_pool.IsRevalidationGenerationCurrent(generation))
                    {
                        return false;
                    }

                    RecordValidation(transactions[i], _pool._specChangeTxValidator.IsWellFormed(transactions[i], _spec));
                }

                Transaction[] blobTransactions = _pool._blobTransactions.GetSnapshot();
                TxLookupKey[] keys = new TxLookupKey[BlobReadBatchSize];
                Hash256[] hashes = new Hash256[BlobReadBatchSize];
                Transaction?[] fullTransactions = new Transaction?[BlobReadBatchSize];
                int batchCount = 0;

                for (int i = 0; i < blobTransactions.Length; i++)
                {
                    if (!_pool.IsRevalidationGenerationCurrent(generation))
                    {
                        return false;
                    }

                    Transaction transaction = blobTransactions[i];
                    if (transaction is not LightTransaction lightTransaction)
                    {
                        RecordValidation(transaction, _pool._specChangeTxValidator.IsWellFormed(transaction, _spec));
                        continue;
                    }

                    Hash256 hash = lightTransaction.Hash!;
                    if (_pool._specChangeTxValidator is ILightTxValidator lightTxValidator)
                    {
                        ValidationResult lightValidation = lightTxValidator.IsWellFormedLight(lightTransaction, _spec);
                        if (!lightValidation)
                        {
                            RecordValidation(lightTransaction, lightValidation);
                            continue;
                        }
                    }

                    keys[batchCount] = new TxLookupKey(hash, lightTransaction.SenderAddress!, lightTransaction.Timestamp);
                    hashes[batchCount] = hash;
                    batchCount++;

                    if (batchCount == BlobReadBatchSize && !ProcessBlobBatch(batchCount, generation, keys, hashes, fullTransactions))
                    {
                        return false;
                    }

                    if (batchCount == BlobReadBatchSize)
                    {
                        batchCount = 0;
                    }
                }

                return batchCount == 0 || ProcessBlobBatch(batchCount, generation, keys, hashes, fullTransactions);
            }

            private bool ProcessBlobBatch(
                int count,
                long generation,
                TxLookupKey[] keys,
                Hash256[] hashes,
                Transaction?[] fullTransactions)
            {
                Array.Clear(fullTransactions, 0, count);
                _pool._blobTxStorage.TryGetMany(keys, count, fullTransactions);

                for (int i = 0; i < count; i++)
                {
                    if (!_pool.IsRevalidationGenerationCurrent(generation))
                    {
                        return false;
                    }

                    Transaction? fullTransaction = fullTransactions[i];
                    if (fullTransaction is null)
                    {
                        _invalidTransactions[hashes[i]] = ForkValidationResult.MissingBody;
                    }
                    else
                    {
                        RecordValidation(fullTransaction, _pool._specChangeTxValidator.IsWellFormed(fullTransaction, _spec));
                        fullTransactions[i] = null;
                    }
                }

                return true;
            }

            private void RecordValidation(Transaction transaction, in ValidationResult validation)
            {
                if (!validation)
                {
                    bool invalidTransactionForm = validation.Error == TxErrorMessages.InvalidTransactionForm;
                    _invalidTransactions[transaction.Hash!] = new ForkValidationResult(
                        validation,
                        validation.Error == TxErrorMessages.InvalidProofVersion,
                        !invalidTransactionForm);
                }
            }
        }

        private readonly record struct ForkValidationResult(
            ValidationResult Validation,
            bool AllowImmediateResubmission,
            bool AllowAfterSpecChange)
        {
            public static ForkValidationResult MissingBody { get; } = new(
                TxErrorMessages.InvalidTransactionForm,
                AllowImmediateResubmission: true,
                AllowAfterSpecChange: false);
        }

        private void UpdateBucket(in AccountStruct account, EnhancedSortedSet<Transaction> transactions, ref Transaction? lastElement, UpdateTransactionDelegate updateTx) =>
            UpdateBucketCore(account, transactions, ref lastElement, updateTx, revalidation: null);

        /// <returns>How many transactions were dropped as invalid under <paramref name="revalidation"/>.</returns>
        private int UpdateBucketCore(
            in AccountStruct account,
            EnhancedSortedSet<Transaction> transactions,
            ref Transaction? lastElement,
            UpdateTransactionDelegate updateTx,
            ForkRevalidation? revalidation)
        {
            if (transactions.Count == 0)
            {
                return 0;
            }

            UInt256 balance = account.Balance;
            ulong currentNonce = account.Nonce;
            Transaction? tx = null;
            foreach (Transaction txn in transactions)
            {
                if (txn.Nonce == currentNonce)
                {
                    tx = txn;
                    break;
                }
            }

            bool shouldBeDumped = tx is null
                || balance < tx.ValueRef
                || !tx.Supports1559 &&
                (UInt256.MultiplyOverflow((UInt256)tx.GasPrice, tx.GasLimit, out UInt256 cost)
                    || UInt256.AddOverflow(cost, tx.Value, out cost)
                    || balance < cost);

            if (shouldBeDumped)
            {
                foreach (Transaction transaction in transactions)
                {
                    // transaction removed from TxPool because of insufficient balance should have opportunity
                    // to come back in the future, so it is removed from long term cache as well.
                    _hashCache.DeleteFromLongTerm(transaction.Hash!);

                    updateTx(transactions, transaction, changedGasBottleneck: null, lastElement);
                }

                return 0;
            }

            return UpdateGasBottleneckAndMarkForEviction(transactions, currentNonce, balance, lastElement, updateTx, revalidation);
        }

        public bool RemoveTransaction(Hash256? hash)
        {
            if (hash is null)
            {
                return false;
            }

            bool hasBeenRemoved = _transactions.TryRemove(hash, out Transaction? transaction)
                                 || _blobTransactions.TryRemove(hash, out transaction);

            if (transaction is null || !hasBeenRemoved)
            {
                return false;
            }

            RemovedPending?.Invoke(this, new TxEventArgs(transaction));

            _broadcaster.StopBroadcast(hash);

            if (_logger.IsTrace) _logger.Trace($"Removed a transaction: {hash}");

            return true;
        }

        public bool ContainsTx(Hash256 hash, TxType txType) => txType == TxType.Blob
            ? _blobTransactions.ContainsKey(hash)
            : _transactions.ContainsKey(hash) || _broadcaster.ContainsTx(hash);

        public bool TryGetPendingTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? transaction) =>
            _transactions.TryGetValue(hash, out transaction)
            || _blobTransactions.TryGetValue(hash, out transaction)
            || _broadcaster.TryGetPersistentTx(hash, out transaction);

        public bool TryGetPendingBlobTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? blobTransaction) =>
            _blobTransactions.TryGetValue(hash, out blobTransaction);

        /// <inheritdoc/>
        public PendingTransactionsView GetPendingForProduction(BlockHeader targetBlock, bool filterToReadyTx, UInt256 baseFee)
        {
            long forkStateVersion = Volatile.Read(ref _forkStateVersion);
            IDictionary<AddressAsKey, Transaction[]> transactions = filterToReadyTx
                ? GetPendingTransactionsBySender(true, baseFee)
                : GetPendingTransactionsBySender();
            IDictionary<AddressAsKey, Transaction[]> blobTransactions = GetPendingLightBlobTransactionsBySender();

            return new(transactions, blobTransactions, IsRevalidatedFor(targetBlock, forkStateVersion));
        }

        /// <summary>
        /// Whether every transaction in the pool has been validated against the specification of
        /// <paramref name="targetBlock"/>.
        /// </summary>
        /// <remarks>
        /// Also requires that specification to be the current chain head one. The fork-state version must remain
        /// stable while a production snapshot is taken; otherwise the snapshot is conservatively reported as
        /// not revalidated and the producer checks its candidates itself.
        /// </remarks>
        internal bool IsRevalidatedFor(BlockHeader targetBlock) =>
            IsRevalidatedFor(targetBlock, Volatile.Read(ref _forkStateVersion));

        private bool IsRevalidatedFor(BlockHeader targetBlock, long forkStateVersion)
        {
            IReleaseSpec targetSpec = _specProvider.GetSpec(targetBlock);
            IReleaseSpec? validatedSpec = Volatile.Read(ref _validatedSpec);
            IReleaseSpec currentSpec = _specProvider.GetCurrentHeadSpec();
            long currentForkStateVersion = Volatile.Read(ref _forkStateVersion);

            return (forkStateVersion & 1) == 0
                && forkStateVersion == currentForkStateVersion
                && ReferenceEquals(targetSpec, validatedSpec)
                && ReferenceEquals(targetSpec, currentSpec);
        }

        // only for tests - to test sorting
        internal void TryGetBlobTxSortingEquivalent(Hash256 hash, out Transaction? transaction)
            => _blobTransactions.TryGetBlobTxSortingEquivalent(hash, out transaction);

        // should own transactions (in broadcaster) be also checked here?
        // maybe it should use NonceManager, as it already has info about local txs?
        public ulong GetLatestPendingNonce(Address address)
        {
            ulong maxPendingNonce = _accounts.GetNonce(address);

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
                    ulong pendingCount = (ulong)transactions.Count;
                    if (maxPendingNonce + pendingCount - 1 == lastTransaction.Nonce)
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

        public Transaction? GetBestTx() => _transactions.GetBest();

        public IEnumerable<Transaction> GetBestTxOfEachSender() => _transactions.GetFirsts();

        public bool IsKnown(Hash256? hash) => hash is not null && _hashCache.Get(hash);

        public event EventHandler<TxEventArgs>? NewDiscovered;
        public event EventHandler<TxEventArgs>? NewPending;
        public event EventHandler<TxEventArgs>? RemovedPending;
        public event EventHandler<TxEventArgs>? EvictedPending;

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _timer?.Dispose();
            await _cts.CancelAsync();
            TxPoolHeadChanged -= _broadcaster.OnNewHead;
            _broadcaster.Dispose();
            _headInfo.HeadChanged -= OnHeadChange;
            _headBlocksChannel.Writer.Complete();
            _transactions.Inserted -= OnInsertedTx;
            _transactions.Removed -= OnRemovedTx;

            await _retryCache.DisposeAsync();
            await _headProcessing;
        }

        private void TimerOnElapsed(object? sender, EventArgs e)
        {
            WriteTxPoolReport(_logger);

            _timer!.Enabled = true;
        }

        internal void ResetAddress(Address address)
        {
            using ArrayPoolList<AddressAsKey> arrayPoolList = new(1);
            arrayPoolList.Add(address);
            _accountCache.RemoveAccounts(arrayPoolList);
        }

        private readonly record struct HeadChange(BlockReplacementEventArgs Args, long Generation);


        private sealed class AccountCache : IAccountStateProvider
        {
            private readonly IAccountStateProvider _provider;
            private readonly ClockCache<AddressAsKey, AccountStruct>[] _caches;

            public AccountCache(IAccountStateProvider provider)
            {
                _provider = provider;
                _caches = new ClockCache<AddressAsKey, AccountStruct>[16];
                for (int i = 0; i < _caches.Length; i++)
                {
                    // Cache per nibble to reduce contention as TxPool is very parallel
                    _caches[i] = new ClockCache<AddressAsKey, AccountStruct>(1_024);
                }
            }

            public bool TryGetAccount(Address address, out AccountStruct account)
            {
                ClockCache<AddressAsKey, AccountStruct> cache = _caches[GetCacheIndex(address)];
                if (!cache.TryGet(new AddressAsKey(address), out account))
                {
                    if (!_provider.TryGetAccount(address, out account))
                    {
                        cache.Set(address, AccountStruct.TotallyEmpty);
                        return false;
                    }
                    cache.Set(address, account);
                }
                else
                {
                    Db.Metrics.AddStateTreeCacheHits(1);
                }

                return true;
            }

            public void RemoveAccounts(ArrayPoolList<AddressAsKey> address) => Parallel.ForEach(address.GroupBy(a => GetCacheIndex(a.Value)),
                    n =>
                    {
                        ClockCache<AddressAsKey, AccountStruct> cache = _caches[n.Key];
                        foreach (AddressAsKey a in n)
                        {
                            cache.Delete(a);
                        }
                    }
                );

            private static int GetCacheIndex(Address address) => address.Bytes[^1] & 0xf;

            public void Reset()
            {
                for (int i = 0; i < _caches.Length; i++)
                {
                    _caches[i].Clear();
                }
            }
        }

        private static void WriteTxPoolReport(in ILogger logger)
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
------------------------------------------------
TxPool: {Metrics.TransactionCount:N0} txns queued
BlobPool: {Metrics.BlobTransactionCount:N0} txns queued
------------------------------------------------
Sent
* Transactions:         {Metrics.PendingTransactionsSent,24:N0}
* Hashes:               {Metrics.PendingTransactionsHashesSent,24:N0}
------------------------------------------------
Received
* Transactions:         {Metrics.PendingTransactionsReceived,24:N0}
* Hashes:               {Metrics.PendingTransactionsHashesReceived,24:N0}
------------------------------------------------
Discarded at Filter Stage:
1.  NotSupportedTxType  {Metrics.PendingTransactionsNotSupportedTxType,24:N0}
2.  Tx Too Large:       {Metrics.PendingTransactionsSizeTooLarge,24:N0}
3.  GasLimitTooHigh:    {Metrics.PendingTransactionsGasLimitTooHigh,24:N0}
4.  TooLow PriorityFee: {Metrics.PendingTransactionsTooLowPriorityFee,24:N0}
5.  TooLow FeePerBlobGa:{Metrics.PendingTransactionsTooLowFeePerBlobGas,24:N0}
6.  Too Low Fee:        {Metrics.PendingTransactionsTooLowFee,24:N0}
7.  Malformed:          {Metrics.PendingTransactionsMalformed,24:N0}
8.  Null Hash:          {Metrics.PendingTransactionsNullHash,24:N0}
9.  Duplicate:          {Metrics.PendingTransactionsKnown,24:N0}
10.  Unknown Sender:    {Metrics.PendingTransactionsUnresolvableSender,24:N0}
11. Conflicting TxType: {Metrics.PendingTransactionsConflictingTxType,24:N0}
12. NonceTooFarInFuture {Metrics.PendingTransactionsNonceTooFarInFuture,24:N0}
13. Zero Balance:       {Metrics.PendingTransactionsZeroBalance,24:N0}
14. Balance < tx.value: {Metrics.PendingTransactionsBalanceBelowValue,24:N0}
15. Balance Too Low:    {Metrics.PendingTransactionsTooLowBalance,24:N0}
16. Nonce used:         {Metrics.PendingTransactionsLowNonce,24:N0}
17. Nonces skipped:     {Metrics.PendingTransactionsNonceGap,24:N0}
18. Failed replacement  {Metrics.PendingTransactionsPassedFiltersButCannotReplace,24:N0}
19. Cannot Compete:     {Metrics.PendingTransactionsPassedFiltersButCannotCompeteOnFees,24:N0}
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
* Blob Added:           {Metrics.PendingBlobTransactionsAdded,24:N0}
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
* Eip7702 txs:          {Metrics.Eip7702TransactionsInBlock,24:N0}
------------------------------------------------
Db usage:
* BlobDb writes:        {Db.Metrics.DbWrites.GetValueOrDefault("BlobTransactions"),24:N0}
* BlobDb reads:         {Db.Metrics.DbReads.GetValueOrDefault("BlobTransactions"),24:N0}
------------------------------------------------
");
        }

        // Cleanup ArrayPoolList AccountChanges as they are not used anywhere else
        private static void DisposeBlockAccountChanges(Block block) => block.DisposeAccountChanges();
    }
}
