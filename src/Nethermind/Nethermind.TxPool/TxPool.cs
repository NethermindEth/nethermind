// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
using Nethermind.Evm.TransactionProcessing;
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
        private readonly ITxValidator? _headTxValidator;
        private readonly bool _blobReorgsSupportEnabled;
        private readonly DelegationCache _pendingDelegations = new();
        private readonly PayerExposureCache _payerExposure = new();

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
        private ulong _lastBlockNumber = ulong.MaxValue;
        private Hash256? _lastBlockHash;

        private bool _isDisposed;
        private long _pendingTransactionsAdded = 0;

        // Count of pending frame txs carrying an EIP-8141 expiry deadline; lets the per-head expiry
        // pass skip the pool walk entirely when there is nothing to expire (the case on every network
        // where the fork is inactive). Maintained under the pool lock via the Inserted/Removed events.
        private int _expiringFrameTxCount;

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
        /// <param name="headTxValidator"></param>
        /// <param name="frameTxPrefixSimulator">Optional EIP-8141 opaque-prefix simulator; unwired on chains without frame transactions.</param>
        public TxPool(IEthereumEcdsa ecdsa,
            IBlobTxStorage blobTxStorage,
            IChainHeadInfoProvider chainHeadInfoProvider,
            ITxPoolConfig txPoolConfig,
            ITxValidator validator,
            ILogManager? logManager,
            IComparer<Transaction> comparer,
            ITxGossipPolicy? transactionsGossipPolicy = null,
            IIncomingTxFilter? incomingTxFilter = null,
            [KeyFilter(ITxValidator.HeadTxValidatorKey)] ITxValidator? headTxValidator = null,
            bool thereIsPriorityContract = false,
            IFrameTxPrefixSimulator? frameTxPrefixSimulator = null)
        {
            _logger = logManager?.GetClassLogger<TxPool>() ?? throw new ArgumentNullException(nameof(logManager));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _blobTxStorage = blobTxStorage ?? throw new ArgumentNullException(nameof(blobTxStorage));
            _headInfo = chainHeadInfoProvider ?? throw new ArgumentNullException(nameof(chainHeadInfoProvider));
            _txPoolConfig = txPoolConfig;
            _headTxValidator = headTxValidator;
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
            // Records restored inside the pool's constructor predate the handlers below, so the count is seeded
            // before subscribing: UpdatePool evicts during startup, and a removal must decrement a count that
            // already covers what it removes.
            if (_blobTransactions.Count > 0)
            {
                foreach (Transaction restored in _blobTransactions.GetSnapshot())
                {
                    if (HasExpiryDeadline(restored)) _expiringFrameTxCount++;
                }
            }

            // EIP-8141: blob-carrying frame txs live in the blob pool, so it wires the same insert/removal
            // bookkeeping (delegations, frame expiry) as the normal pool.
            _blobTransactions.Inserted += OnInsertedTx;
            _blobTransactions.Removed += OnRemovedTx;
            if (_blobTransactions.Count > 0)
            {
                _blobTransactions.UpdatePool(_accounts, _updateBucket);
            }

            _headInfo.HeadChanged += OnHeadChange;

            _preHashFilters =
            [
                new NotSupportedTxFilter(txPoolConfig, _specProvider, _logger),
                new SizeTxFilter(txPoolConfig, _logger),
                new GasLimitTxFilter(_headInfo, txPoolConfig, logManager),
                new PriorityFeeTooLowFilter(_headInfo, txPoolConfig, _logger),
                new FeeTooLowFilter(_headInfo, _transactions, _blobTransactions, thereIsPriorityContract, _logger)
            ];

            List<IIncomingTxFilter> postHashFilters =
            [
                new NullHashTxFilter(), // needs to be first as it assigns the hash
                new AlreadyKnownTxFilter(_hashCache, _logger),
                new MalformedTxFilter(_specProvider, validator, ecdsa, _logger),
                new ExpiredFrameTxFilter(chainHeadInfoProvider, _logger), // after MalformedTxFilter: reads the deadline from an already well-formed frame
                new FrameTxVerifyGasFilter(txPoolConfig, _logger), // after MalformedTxFilter: reads gas limits from an already well-formed frame list
                new FrameTxPayerlessFilter(_logger), // before FrameTxSignatureFilter: a structural payerless verdict needs no signature work

                new TxTypeTxFilter(_transactions,
                    _blobTransactions), // has to be after MalformedTxFilter as it uses the recovered sender
                new BalanceZeroFilter(thereIsPriorityContract, _logger),
                new BalanceTooLowFilter(_transactions, _blobTransactions, _logger),
                new LowNonceFilter(_logger), // has to be after MalformedTxFilter as it uses the recovered sender
                new FutureNonceFilter(txPoolConfig),
                new GapNonceFilter(_transactions, _blobTransactions, _logger),
                new KeyedNonceFilter(chainHeadInfoProvider.ReadOnlyStateProvider), // the three above skip keyed sets, this one owns them
                new RecoverAuthorityFilter(ecdsa),
                new DelegatedAccountFilter(_specProvider, _transactions, _blobTransactions, chainHeadInfoProvider.ReadOnlyStateProvider, _pendingDelegations),
                new FrameTxSignatureFilter(_specProvider, ecdsa, _logger), // last: elliptic-curve work over an uncapped signature list, so let the cheap filters reject first
            ];

            if (incomingTxFilter is not null)
            {
                postHashFilters.Add(incomingTxFilter);
            }

            postHashFilters.Add(new DeployedCodeFilter(chainHeadInfoProvider.ReadOnlyStateProvider, _specProvider));

            // EIP-8141: resolve last, so only otherwise-admissible frame txs are resolved.
            postHashFilters.Add(new FrameTxPayerFilter(chainHeadInfoProvider.ReadOnlyStateProvider, _logger));

            // EIP-8141: runs after FrameTxPayerFilter so the natively-resolved fast path bypasses it.
            // Optional: when unwired, opaque frame txs stay deferred as in Phase 1.
            postHashFilters.Add(new FrameTxSimulationFilter(chainHeadInfoProvider.ReadOnlyStateProvider, frameTxPrefixSimulator, _logger));

            // EIP-8141: must follow both resolvers — it prices whichever payer they recorded, and a
            // second registration would reserve every frame tx's cost twice.
            postHashFilters.Add(new FrameTxPayerExposureFilter(_specProvider, chainHeadInfoProvider.ReadOnlyStateProvider, _transactions, _blobTransactions, _payerExposure, _logger));

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
                (data => data.first.CanPayBaseFee(baseFee) && IsNonceReady(data.first, data.key)) :
                null);

        /// <summary>Whether <paramref name="tx"/> carries the nonce its sender can consume in the next block.</summary>
        /// <remarks>
        /// An <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> keyed set does not use the account
        /// nonce, so readiness is per-key currency instead.
        /// </remarks>
        private bool IsNonceReady(Transaction tx, Address sender) =>
            KeyedNonceManager.UsesKeyedNonce(tx)
                ? IsKeyedNonceCurrent(tx)
                : tx.Nonce == _accounts.GetNonce(sender);

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

        private void OnInsertedTx(object? sender, SortedPool<ValueHash256, Transaction, AddressAsKey>.SortedPoolEventArgs args)
        {
            AddPendingDelegations(args.Value);
            if (HasExpiryDeadline(args.Value)) Interlocked.Increment(ref _expiringFrameTxCount);
        }

        private void OnRemovedTx(object? sender, SortedPool<ValueHash256, Transaction, AddressAsKey>.SortedPoolRemovedEventArgs args)
        {
            RemovePendingDelegations(args.Value);
            if (HasExpiryDeadline(args.Value)) Interlocked.Decrement(ref _expiringFrameTxCount);
            ReleasePayerExposure(args.Value);
        }

        private static bool HasExpiryDeadline(Transaction tx) => tx.SupportsFrames && FrameTxValidation.TryGetExpiryDeadline(tx, out _);

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
                        RemoveExpiredFrameTransactions(args.Block);

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
                    // Un-mark the hash first: a blob-carrying tx (type-3 or type-6 frame) is re-added below only
                    // from blob storage, and a dropped one must not stay AlreadyKnown or the sender cannot resend.
                    _hashCache.Delete(tx.Hash!);
                    if (tx.CarriesBlobs)
                    {
                        continue;
                    }
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

                if (blockTx.CarriesBlobs)
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

        /// <summary>
        /// Drops pending EIP-8141 frame transactions whose expiry deadline has passed as of the new head.
        /// </summary>
        /// <remarks>
        /// An expired frame tx can never be included (the expiry-verifier predeploy reverts once
        /// <c>block.timestamp &gt; deadline</c>), so it is evicted rather than re-propagated
        /// (ethereum/EIPs#12007, "Revalidation"). The predicate matches that revert condition exactly (not
        /// <c>&gt;=</c>) so the pool never drops a tx the predeploy would accept.
        /// The count guard skips the pool walk when no expiring frame tx is present.
        /// EIP8141: a deadline-ordered index (evict without scanning) is deferred to the scalable eviction layer;
        /// so is sweeping expired frame txs held only in the broadcaster's persistent-broadcast pool (locally
        /// submitted, then cheap-evicted from <see cref="_transactions"/>), which this pass does not yet reach.
        /// </remarks>
        private void RemoveExpiredFrameTransactions(Block block)
        {
            if (Volatile.Read(ref _expiringFrameTxCount) == 0
                || !_specProvider.GetSpec(block.Header).IsEip8141Enabled)
            {
                return;
            }

            ulong timestamp = block.Timestamp;
            EvictExpiredFrameTransactions(_transactions.GetSnapshot(), timestamp);
            EvictExpiredFrameTransactions(_blobTransactions.GetSnapshot(), timestamp);
        }

        private void EvictExpiredFrameTransactions(Transaction[] snapshot, ulong timestamp)
        {
            for (int i = 0; i < snapshot.Length; i++)
            {
                Transaction tx = snapshot[i];
                if (tx.SupportsFrames
                    && FrameTxValidation.TryGetExpiryDeadline(tx, out ulong deadline)
                    && timestamp > deadline)
                {
                    if (RemoveTransaction(tx.Hash))
                    {
                        // Surface as a genuine drop to eth_subscribe("droppedPendingTransactions"): an expired
                        // frame tx can never be included. Unlike a capacity eviction it is deliberately left in
                        // _hashCache (RemoveTransaction does not touch it) so it cannot re-enter the pool.
                        EvictedPending?.Invoke(this, new TxEventArgs(tx));
                        Metrics.PendingTransactionsEvicted++;
                        if (_logger.IsTrace) _logger.Trace($"Evicted expired frame transaction {tx.Hash} (deadline {deadline} < head timestamp {timestamp}).");
                    }
                }
            }
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

            TxFilteringState state = new(tx, _accounts);
            AcceptTxResult accepted = AcceptTxResult.Invalid;

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
            // EIP-8141: a successful insert hands the payer reservation to the pool, released on Removed.
            // Every other exit, a throw included, must release it here or it leaks for good.
            bool reservationSettled = false;
            try
            {
                bool eip1559Enabled = _specProvider.GetCurrentHeadSpec().IsEip1559Enabled;
                UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(eip1559Enabled, _headInfo.CurrentBaseFee);
                TxDistinctSortedPool relevantPool = (tx.CarriesBlobs ? _blobTransactions : _transactions);

                relevantPool.TryGetBucketsWorstValue(tx.SenderAddress!, out Transaction? worstTx);
                tx.GasBottleneck = (worstTx is null || effectiveGasPrice <= worstTx.GasBottleneck)
                    ? effectiveGasPrice
                    : worstTx.GasBottleneck;

                bool inserted = relevantPool.TryInsert(tx.Hash!, tx, out Transaction? removed);
                // The reservation is now the pool's, or was already released by a self-eviction Removed.
                reservationSettled = true;

                if (!inserted)
                {
                    // it means it failed on adding to the pool - it is possible when new tx has the same sender
                    // and nonce as already existent tx and is not good enough to replace it
                    // No Removed event fires for this tx, so release the reservation it took.
                    ReleasePayerExposure(tx);
                    Metrics.PendingTransactionsPassedFiltersButCannotReplace++;
                    return AcceptTxResult.ReplacementNotAllowed;
                }

                if (tx.Hash == removed?.Hash)
                {
                    // it means it was added and immediately evicted - pool was full of better txs
                    // Its Removed already released the reservation, so a tx kept only by the broadcaster
                    // under-counts its payer — accepted, since the broadcaster has no hook to release on.
                    if (!isPersistentBroadcast || tx.CarriesBlobs || !_broadcaster.Broadcast(tx, true))
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
                if (tx.CarriesBlobs) { Metrics.PendingBlobTransactionsAdded++; }

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
            finally
            {
                if (!reservationSettled)
                {
                    ReleasePayerExposure(tx);
                }
            }
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

        /// <summary>
        /// Releases the pending exposure a resolved frame-tx payer reserved at admission
        /// (<see cref="FrameTxPayerExposureFilter"/>) once the transaction leaves the pool.
        /// </summary>
        /// <remarks>
        /// Covers eviction, replacement, inclusion and reorg removal (all funnel through the pool
        /// <c>Removed</c> event) plus the paths in <see cref="AddCore"/> that never insert. Prices with
        /// the same shared helper the reservation used so the two amounts cannot disagree.
        /// </remarks>
        private void ReleasePayerExposure(Transaction tx)
        {
            if (tx.PayerAddress is not null && tx.PayerExposure is { } maxCost)
            {
                _payerExposure.Subtract(tx.PayerAddress, maxCost);
            }
        }

        private void UpdateBucketWithAddedTransaction(in AccountStruct account, EnhancedSortedSet<Transaction> transactions, ref Transaction? lastElement, UpdateTransactionDelegate updateTx)
        {
            if (transactions.Count != 0)
            {
                UInt256 balance = account.Balance;
                ulong currentNonce = account.Nonce;

                UpdateGasBottleneckAndMarkForEviction(transactions, currentNonce, balance, lastElement, updateTx);
            }
        }

        /// <summary>Whether every nonce key <paramref name="tx"/> selects still sits at its declared sequence in the head state.</summary>
        private bool IsKeyedNonceCurrent(Transaction tx) =>
            KeyedNonceManager.IsNonceSetValid(_headInfo.ReadOnlyStateProvider, tx.SenderAddress!, tx.NonceKeys!, tx.Nonce);

        /// <summary>Whether any nonce key <paramref name="tx"/> selects has advanced past its declared sequence in the head state.</summary>
        /// <remarks>A keyed sequence only ever advances, so a key already beyond the declared sequence has spent it for good on this fork.</remarks>
        private bool IsKeyedNonceBehind(Transaction tx)
        {
            foreach (UInt256 nonceKey in tx.NonceKeys!)
            {
                if (KeyedNonceManager.CurrentNonceSeq(_headInfo.ReadOnlyStateProvider, tx.SenderAddress!, in nonceKey) > tx.Nonce)
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateGasBottleneckAndMarkForEviction(
            EnhancedSortedSet<Transaction> transactions,
            ulong currentNonce,
            UInt256 balance,
            Transaction? lastElement,
            UpdateTransactionDelegate updateTx)
        {
            UInt256? previousTxBottleneck = null;
            int i = 0;
            UInt256 cumulativeCost = 0;
            IReleaseSpec headSpec = _specProvider.GetCurrentHeadSpec();
            bool isEip1559 = headSpec.IsEip1559Enabled;
            bool evictNextTxs = false;

            foreach (Transaction tx in transactions)
            {
                if (KeyedNonceManager.UsesKeyedNonce(tx))
                {
                    if (!IsKeyedNonceCurrent(tx))
                    {
                        MarkForEviction(tx, allowLaterPoolReentrance: !IsKeyedNonceBehind(tx));
                    }
                    else
                    {
                        ValidationResult keyedValid = _headTxValidator?.IsWellFormed(tx, headSpec) ?? ValidationResult.Success;
                        if (!keyedValid)
                        {
                            MarkForEviction(tx, keyedValid.Error == TxErrorMessages.InvalidProofVersion);
                        }
                        else if (tx.CheckForNotEnoughBalance(UInt256.Zero, balance, out _))
                        {
                            MarkForEviction(tx, allowLaterPoolReentrance: true);
                        }
                        else
                        {
                            UInt256 keyedBottleneck = tx.CalculateEffectiveGasPrice(isEip1559, _headInfo.CurrentBaseFee);
                            if (tx.GasBottleneck != keyedBottleneck)
                            {
                                updateTx(transactions, tx, keyedBottleneck, lastElement);
                            }
                        }
                    }

                    continue;
                }

                if (tx.Nonce < currentNonce)
                {
                    MarkForEviction(tx, false);
                    continue;
                }

                try
                {
                    UInt256 gasBottleneck = 0;

                    ValidationResult valid = _headTxValidator?.IsWellFormed(tx, headSpec) ?? ValidationResult.Success;

                    if (!valid)
                    {
                        MarkForEviction(tx, valid.Error == TxErrorMessages.InvalidProofVersion);
                        continue;
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

            void MarkForEviction(Transaction tx, bool allowLaterPoolReentrance)
            {
                _broadcaster.StopBroadcast(tx.Hash!);
                if (allowLaterPoolReentrance) _hashCache.DeleteFromLongTerm(tx.Hash!);
                updateTx(transactions, tx, null, lastElement);
                // evict all following txs to prevent nonce gaps between blob tx
                evictNextTxs |= tx.CarriesBlobs;
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
                ulong currentNonce = account.Nonce;
                Transaction? tx = null;
                foreach (Transaction txn in transactions)
                {
                    if (KeyedNonceManager.UsesKeyedNonce(txn) ? IsKeyedNonceCurrent(txn) : txn.Nonce == currentNonce)
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
                else if (balance < tx.ValueRef)
                {
                    shouldBeDumped = true;
                }
                else if (!tx.Supports1559)
                {
                    shouldBeDumped = UInt256.MultiplyOverflow((UInt256)tx.GasPrice, tx.GasLimit, out UInt256 cost);
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
                    UpdateGasBottleneckAndMarkForEviction(transactions, currentNonce, balance, lastElement, updateTx);
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

            RemovedPending?.Invoke(this, new TxEventArgs(transaction));

            _broadcaster.StopBroadcast(hash);

            if (_logger.IsTrace) _logger.Trace($"Removed a transaction: {hash}");

            return true;
        }

        /// <summary>
        /// Removes a frame transaction that block production dropped because its frames did not approve payment,
        /// reporting it as a genuine drop and clearing the long-term cache so it can re-enter once the state its
        /// frames read changes.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="RemoveExpiredFrameTransactions"/>, which keeps the hash so a transaction that can never
        /// be included does not come back, this clears it: the reason a frame transaction fails to pay in the
        /// producer (an unfunded payer, a VERIFY frame reading storage) is chain state that can change. Mirrors the
        /// capacity-eviction path, which likewise raises <see cref="EvictedPending"/> and clears the cache.
        /// </remarks>
        public bool EvictTransaction(Transaction tx)
        {
            if (!RemoveTransaction(tx.Hash)) return false;

            EvictedPending?.Invoke(this, new TxEventArgs(tx));
            _hashCache.DeleteFromLongTerm(tx.Hash!);
            Metrics.PendingTransactionsEvicted++;
            return true;
        }

        public bool ContainsTx(Hash256 hash, TxType txType) => txType == TxType.Blob
            ? _blobTransactions.ContainsKey(hash)
            // EIP-8141: a type-6 frame tx may carry blobs (blob pool) or not (normal pool), so check both.
            : _transactions.ContainsKey(hash)
                || (txType == TxType.FrameTx && _blobTransactions.ContainsKey(hash))
                || _broadcaster.ContainsTx(hash);

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
            _blobTransactions.Inserted -= OnInsertedTx;
            _blobTransactions.Removed -= OnRemovedTx;
            // Removed no longer fires, so anything still reserved would be counted by a gauge no pool can decrement.
            _payerExposure.Clear();

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
