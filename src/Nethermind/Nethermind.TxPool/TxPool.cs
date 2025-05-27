// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CkzgLib;
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
        private readonly bool _blobReorgsSupportEnabled;
        private readonly DelegationCache _pendingDelegations = new();

        private readonly ILogger _logger;

        private readonly Channel<BlockReplacementEventArgs> _headBlocksChannel = Channel.CreateUnbounded<BlockReplacementEventArgs>(new UnboundedChannelOptions() { SingleReader = true, SingleWriter = true });
        private readonly ReaderWriterLockSlim _newHeadLock = new(LockRecursionPolicy.SupportsRecursion);

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
        private Transaction[]? _transactionSnapshot;
        private Transaction[]? _blobTransactionSnapshot;
        private long _lastBlockNumber = -1;
        private Hash256? _lastBlockHash;

        private bool _isDisposed;

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
            IBlobTxStorage blobTxStorage,
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
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _blobTxStorage = blobTxStorage ?? throw new ArgumentNullException(nameof(blobTxStorage));
            _headInfo = chainHeadInfoProvider ?? throw new ArgumentNullException(nameof(chainHeadInfoProvider));
            _txPoolConfig = txPoolConfig;
            _blobReorgsSupportEnabled = txPoolConfig.BlobsSupport.SupportsReorgs();
            _accounts = _accountCache = new AccountCache(_headInfo.ReadOnlyStateProvider);
            _specProvider = _headInfo.SpecProvider;
            SupportsBlobs = _txPoolConfig.BlobsSupport != BlobsSupportMode.Disabled;
            _cts = new();

            MemoryAllowance.MemPoolSize = txPoolConfig.Size;

            // Capture closures once rather than per invocation
            _updateBucket = UpdateBucket;
            _updateBucketAdded = UpdateBucketWithAddedTransaction;

            _broadcaster = new TxBroadcaster(comparer, TimerFactory.Default, txPoolConfig, chainHeadInfoProvider, logManager, transactionsGossipPolicy);
            TxPoolHeadChanged += _broadcaster.OnNewHead;

            _transactions = new TxDistinctSortedPool(MemoryAllowance.MemPoolSize, comparer, logManager);
            _transactions.Removed += OnRemovedTx;

            _blobTransactions = txPoolConfig.BlobsSupport.IsPersistentStorage()
                ? new PersistentBlobTxDistinctSortedPool(blobTxStorage, _txPoolConfig, comparer, logManager)
                : new BlobTxDistinctSortedPool(txPoolConfig.BlobsSupport == BlobsSupportMode.InMemory ? _txPoolConfig.InMemoryBlobPoolSize : 0, comparer, logManager);
            if (_blobTransactions.Count > 0)
                _blobTransactions.UpdatePool(_accounts, _updateBucket);

            _headInfo.HeadChanged += OnHeadChange;

            _preHashFilters =
            [
                new NotSupportedTxFilter(txPoolConfig, _logger),
                new SizeTxFilter(txPoolConfig, _logger),
                new GasLimitTxFilter(_headInfo, txPoolConfig, _logger),
                new PriorityFeeTooLowFilter(_logger),
                new FeeTooLowFilter(_headInfo, _transactions, _blobTransactions, thereIsPriorityContract, _logger),
                new MalformedTxFilter(_specProvider, validator, _logger)
            ];

            List<IIncomingTxFilter> postHashFilters =
            [
                new NullHashTxFilter(), // needs to be first as it assigns the hash
                new AlreadyKnownTxFilter(_hashCache, _logger),
                new UnknownSenderFilter(ecdsa, _logger),
                new TxTypeTxFilter(_transactions,
                    _blobTransactions), // has to be after UnknownSenderFilter as it uses sender
                new BalanceZeroFilter(thereIsPriorityContract, _logger),
                new BalanceTooLowFilter(_transactions, _blobTransactions, _logger),
                new LowNonceFilter(_logger), // has to be after UnknownSenderFilter as it uses sender
                new FutureNonceFilter(txPoolConfig),
                new GapNonceFilter(_transactions, _blobTransactions, _logger),
                new RecoverAuthorityFilter(ecdsa),
                new DelegatedAccountFilter(_specProvider, _transactions, _blobTransactions, chainHeadInfoProvider.ReadOnlyStateProvider, chainHeadInfoProvider.CodeInfoRepository, _pendingDelegations),
            ];

            if (incomingTxFilter is not null)
            {
                postHashFilters.Add(incomingTxFilter);
            }

            postHashFilters.Add(new DeployedCodeFilter(chainHeadInfoProvider.ReadOnlyStateProvider, chainHeadInfoProvider.CodeInfoRepository, _specProvider));

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

        public Transaction[] GetPendingTransactions() => _transactionSnapshot ??= _transactions.GetSnapshot();

        public int GetPendingTransactionsCount() => _transactions.Count;

        public IDictionary<AddressAsKey, Transaction[]> GetPendingTransactionsBySender(bool filterToReadyTx = false, UInt256 baseFee = default) =>
            _transactions.GetBucketSnapshot(filterToReadyTx ?
                (data => data.first.CanPayBaseFee(baseFee) && data.first.Nonce == _accounts.GetNonce(data.key)) :
                null);

        public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender() =>
            _blobTransactions.GetBucketSnapshot();

        public Transaction[] GetPendingTransactionsBySender(Address address) =>
            _transactions.GetBucketSnapshot(address);

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

        public int GetBlobCounts(byte[][] blobVersionedHashes)
            => _blobTransactions.GetBlobCounts(blobVersionedHashes);

        private void OnRemovedTx(object? sender, SortedPool<ValueHash256, Transaction, AddressAsKey>.SortedPoolRemovedEventArgs args)
        {
            RemovePendingDelegations(args.Value);
        }
        private void OnHeadChange(object? sender, BlockReplacementEventArgs e)
        {
            if (_headInfo.IsSyncing)
            {
                DisposeBlockAccountChanges(e.Block);
                return;
            }

            try
            {
                _headBlocksChannel.Writer.TryWrite(e);
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
            while (await _headBlocksChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_headBlocksChannel.Reader.TryRead(out BlockReplacementEventArgs? args))
                {
                    // Clear snapshot
                    _transactionSnapshot = null;
                    _blobTransactionSnapshot = null;

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

                        UpdateBuckets();
                        TxPoolHeadChanged?.Invoke(this, args.Block);
                        Metrics.TransactionCount = _transactions.Count;
                        Metrics.BlobTransactionCount = _blobTransactions.Count;
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsWarn) _logger.Warn($"TxPool failed to update after block {args.Block.ToString(Block.Format.FullHashAndNumber)} with exception {e}");
                    }
                    finally
                    {
                        _newHeadLock.ExitWriteLock();
                    }
                }
            }

            bool CanUseCache(Block block, [NotNullWhen(true)] ArrayPoolList<AddressAsKey>? accountChanges)
            {
                return accountChanges is not null && block.ParentHash == _lastBlockHash && _lastBlockNumber + 1 == block.Number;
            }
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

                if (_blobReorgsSupportEnabled
                    && _blobTxStorage.TryGetBlobTransactionsFromBlock(previousBlock.Number, out Transaction[]? blobTxs)
                    && blobTxs is not null)
                {
                    foreach (Transaction blobTx in blobTxs)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Readded tx {blobTx.Hash} from reorged block {previousBlock.Number} (hash {previousBlock.Hash}) to blob pool");
                        _hashCache.Delete(blobTx.Hash!);
                        blobTx.SenderAddress ??= _ecdsa.RecoverAddress(blobTx);
                        SubmitTx(blobTx, isEip155Enabled ? TxHandlingOptions.None : TxHandlingOptions.PreEip155Signing);
                    }
                    if (_logger.IsDebug) _logger.Debug($"Readded txs from reorged block {previousBlock.Number} (hash {previousBlock.Hash}) to blob pool");

                    _blobTxStorage.DeleteBlobTransactionsFromBlock(previousBlock.Number);
                }
            }
        }

        private void RemoveProcessedTransactions(Block block)
        {
            Transaction[] blockTransactions = block.Transactions;
            using ArrayPoolList<Transaction> blobTxsToSave = new((int)_specProvider.GetSpec(block.Header).MaxBlobCount);
            long discoveredForPendingTxs = 0;
            long discoveredForHashCache = 0;
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
                    blobs += blockTx.GetBlobCount();

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

                if (!IsKnown(txHash))
                {
                    discoveredForHashCache++;
                }

                if (!RemoveIncludedTransaction(blockTx))
                {
                    discoveredForPendingTxs++;
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
                    _broadcaster.AnnounceOnce(peer, _transactionSnapshot ??= _transactions.GetSnapshot());
                    _broadcaster.AnnounceOnce(peer, _blobTransactionSnapshot ??= _blobTransactions.GetSnapshot());
                    if (_logger.IsTrace) _logger.Trace($"Announced {_transactionSnapshot.Length} txs and {_blobTransactionSnapshot.Length} blob txs to peer {peer}");
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

        public AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions handlingOptions)
        {
            if (!AcceptTxWhenNotSynced &&
                _headInfo.IsSyncing &&
                // If local tx allow it to be accepted even when syncing
                (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0)
            {
                return AcceptTxResult.Syncing;
            }

            Metrics.PendingTransactionsReceived++;

            // assign a sequence number to transaction so we can order them by arrival times when
            // gas prices are exactly the same
            tx.PoolIndex = Interlocked.Increment(ref _txIndex);

            NewDiscovered?.Invoke(this, new TxEventArgs(tx));

            bool startBroadcast = (handlingOptions & TxHandlingOptions.PersistentBroadcast) ==
                                  TxHandlingOptions.PersistentBroadcast;

            if (_logger.IsTrace)
            {
                TraceTx(tx, handlingOptions, startBroadcast);
            }

            TryConvertProofVersion(tx);

            TxFilteringState state = new(tx, _accounts);
            AcceptTxResult accepted;

            _newHeadLock.EnterReadLock();
            try
            {
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

            if (accepted)
            {
                // Clear proper snapshot
                if (tx.SupportsBlobs)
                    _blobTransactionSnapshot = null;
                else
                    _transactionSnapshot = null;
            }

            return accepted;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceTx(Transaction tx, TxHandlingOptions handlingOptions, bool startBroadcast)
            {
                bool managedNonce = (handlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
                _logger.Trace($"Adding transaction {tx.ToString("  ")} - managed nonce: {managedNonce} | persistent broadcast {startBroadcast}");
            }
        }

        private void TryConvertProofVersion(Transaction tx)
        {
            if (_txPoolConfig.ProofsTranslationEnabled
                && tx is { SupportsBlobs: true, NetworkWrapper: ShardBlobNetworkWrapper { Version: ProofVersion.V0 } wrapper }
                && _headInfo.CurrentProofVersion == ProofVersion.V1)
            {
                using ArrayPoolList<byte[]> cellProofs = new(Ckzg.CellsPerExtBlob * wrapper.Blobs.Length);

                foreach (byte[] blob in wrapper.Blobs)
                {
                    using ArrayPoolSpan<byte> cellProofsOfOneBlob = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerProof);
                    KzgPolynomialCommitments.ComputeCellProofs(blob, cellProofsOfOneBlob);
                    byte[][] cellProofsSeparated = cellProofsOfOneBlob.Chunk(Ckzg.BytesPerProof).ToArray();
                    cellProofs.AddRange(cellProofsSeparated);
                }

                tx.NetworkWrapper = wrapper with { Proofs = [.. cellProofs], Version = ProofVersion.V1 };
            }
        }

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
            if (tx.Supports1559) { Metrics.Pending1559TransactionsAdded++; }
            if (tx.SupportsBlobs) { Metrics.PendingBlobTransactionsAdded++; }

            if (removed is not null)
            {
                RemovePendingDelegations(removed);
                EvictedPending?.Invoke(this, new TxEventArgs(removed));
                // transaction which was on last position in sorted TxPool and was deleted to give
                // a place for a newly added tx (with higher priority) is now removed from hashCache
                // to give it opportunity to come back to TxPool in the future, when fees drops
                _hashCache.DeleteFromLongTerm(removed.Hash!);
                Metrics.PendingTransactionsEvicted++;
            }

            AddPendingDelegations(tx);

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
                foreach (var auth in transaction.AuthorizationList)
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
                long currentNonce = (long)(account.Nonce);

                UpdateGasBottleneck(transactions, currentNonce, balance, lastElement, updateTx);
            }
        }

        private void UpdateGasBottleneck(
            EnhancedSortedSet<Transaction> transactions, long currentNonce, UInt256 balance, Transaction? lastElement, UpdateTransactionDelegate updateTx)
        {
            UInt256? previousTxBottleneck = null;
            int i = 0;
            UInt256 cumulativeCost = 0;
            IReleaseSpec headSpec = _specProvider.GetCurrentHeadSpec();
            bool dropBlobs = false;

            foreach (Transaction tx in transactions)
            {
                UInt256 gasBottleneck = 0;

                if (tx.Nonce < currentNonce)
                {
                    _broadcaster.StopBroadcast(tx.Hash!);
                    updateTx(transactions, tx, changedGasBottleneck: null, lastElement);
                }
                else
                {
                    dropBlobs |= tx.SupportsBlobs && (tx.GetProofVersion() != headSpec.BlobProofVersion || (ulong)tx.BlobVersionedHashes!.Length > headSpec.MaxBlobCount);

                    if (dropBlobs)
                    {
                        _hashCache.DeleteFromLongTerm(tx.Hash!);
                        updateTx(transactions, tx, changedGasBottleneck: null, lastElement);
                        continue;
                    }

                    previousTxBottleneck ??= tx.CalculateAffordableGasPrice(_specProvider.GetCurrentHeadSpec().IsEip1559Enabled,
                            _headInfo.CurrentBaseFee, balance);

                    // it is not affecting non-blob txs - for them MaxFeePerBlobGas is null so check is skipped
                    if (tx.MaxFeePerBlobGas < _headInfo.CurrentFeePerBlobGas)
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
                            updateTx(transactions, tx, changedGasBottleneck: null, lastElement);
                        }
                        gasBottleneck = UInt256.Min(effectiveGasPrice, previousTxBottleneck ?? 0);
                    }

                    if (tx.GasBottleneck != gasBottleneck)
                    {
                        updateTx(transactions, tx, gasBottleneck, lastElement);
                    }

                    previousTxBottleneck = gasBottleneck;
                    i++;
                }
            }
        }

        private void UpdateBuckets()
        {
            _transactions.UpdatePool(_accounts, _updateBucket);
            _blobTransactions.UpdatePool(_accounts, _updateBucket);
        }

        private void UpdateBucket(in AccountStruct account, EnhancedSortedSet<Transaction> transactions, ref Transaction? lastElement, UpdateTransactionDelegate updateTx)
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

                        updateTx(transactions, transaction, changedGasBottleneck: null, lastElement);
                    }
                }
                else
                {
                    UpdateGasBottleneck(transactions, currentNonce, balance, lastElement, updateTx);
                }
            }
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

            if (hasBeenRemoved)
            {
                RemovedPending?.Invoke(this, new TxEventArgs(transaction));

                RemovePendingDelegations(transaction);
            }

            _broadcaster.StopBroadcast(hash);

            if (_logger.IsTrace) _logger.Trace($"Removed a transaction: {hash}");

            return hasBeenRemoved;
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

        // only for tests - to test sorting
        internal void TryGetBlobTxSortingEquivalent(Hash256 hash, out Transaction? transaction)
            => _blobTransactions.TryGetBlobTxSortingEquivalent(hash, out transaction);

        // should own transactions (in broadcaster) be also checked here?
        // maybe it should use NonceManager, as it already has info about local txs?
        public UInt256 GetLatestPendingNonce(Address address)
        {
            UInt256 maxPendingNonce = _accounts.GetNonce(address);

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
            _cts.Cancel();
            TxPoolHeadChanged -= _broadcaster.OnNewHead;
            _broadcaster.Dispose();
            _headInfo.HeadChanged -= OnHeadChange;
            _headBlocksChannel.Writer.Complete();
            _transactions.Removed -= OnRemovedTx;

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
                var cache = _caches[GetCacheIndex(address)];
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
                    Db.Metrics.IncrementStateTreeCacheHits();
                }

                return true;
            }

            public void RemoveAccounts(ArrayPoolList<AddressAsKey> address)
            {
                Parallel.ForEach(address.GroupBy(a => GetCacheIndex(a.Value)),
                    n =>
                    {
                        ClockCache<AddressAsKey, AccountStruct> cache = _caches[n.Key];
                        foreach (AddressAsKey a in n)
                        {
                            cache.Delete(a);
                        }
                    }
                );
            }

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
2.  GasLimitTooHigh:    {Metrics.PendingTransactionsGasLimitTooHigh,24:N0}
3.  TooLow PriorityFee: {Metrics.PendingTransactionsTooLowPriorityFee,24:N0}
4.  Too Low Fee:        {Metrics.PendingTransactionsTooLowFee,24:N0}
5.  Malformed           {Metrics.PendingTransactionsMalformed,24:N0}
6.  Duplicate:          {Metrics.PendingTransactionsKnown,24:N0}
7.  Unknown Sender:     {Metrics.PendingTransactionsUnresolvableSender,24:N0}
8.  Conflicting TxType  {Metrics.PendingTransactionsConflictingTxType,24:N0}
9.  NonceTooFarInFuture {Metrics.PendingTransactionsNonceTooFarInFuture,24:N0}
10. Zero Balance:       {Metrics.PendingTransactionsZeroBalance,24:N0}
11. Balance < tx.value: {Metrics.PendingTransactionsBalanceBelowValue,24:N0}
12. Balance Too Low:    {Metrics.PendingTransactionsTooLowBalance,24:N0}
13. Nonce used:         {Metrics.PendingTransactionsLowNonce,24:N0}
14. Nonces skipped:     {Metrics.PendingTransactionsNonceGap,24:N0}
15. Failed replacement  {Metrics.PendingTransactionsPassedFiltersButCannotReplace,24:N0}
16. Cannot Compete:     {Metrics.PendingTransactionsPassedFiltersButCannotCompeteOnFees,24:N0}
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
        private static void DisposeBlockAccountChanges(Block block)
        {
            if (block.AccountChanges is null) return;

            block.AccountChanges.Dispose();
            block.AccountChanges = null;
        }
    }
}

