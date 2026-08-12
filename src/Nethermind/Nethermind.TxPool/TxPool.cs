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
using Nethermind.Evm.State;
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
        private readonly FrameTxDependencyIndex _frameDependencies = new();
        private readonly HashSet<ValueHash256> _frameTxsToRevalidate = [];
        private readonly IFrameTxPrefixSimulator? _frameTxPrefixSimulator;

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
        private volatile Transaction[]? _transactionSnapshot;
        private volatile Transaction[]? _blobTransactionSnapshot;
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
            _frameTxPrefixSimulator = frameTxPrefixSimulator;
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
            // EIP-8141: blob-carrying frame txs live in the blob pool, so it wires the same insert/removal
            // bookkeeping (delegations, frame expiry) as the normal pool. Frame expiry only takes effect
            // under BlobsSupportMode.InMemory: persistent storage keeps a LightTransaction whose Type is
            // hard-coded TxType.Blob, so SupportsFrames is false until the light record carries the tx type.
            _blobTransactions.Inserted += OnInsertedTx;
            _blobTransactions.Removed += OnRemovedTx;
            if (_blobTransactions.Count > 0)
                _blobTransactions.UpdatePool(_accounts, _updateBucket);

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
                new RecoverAuthorityFilter(ecdsa),
                new DelegatedAccountFilter(_specProvider, _transactions, _blobTransactions, chainHeadInfoProvider.ReadOnlyStateProvider, _pendingDelegations),
                new FrameTxSignatureFilter(_specProvider, ecdsa, _logger), // last: elliptic-curve work over an uncapped signature list, so let the cheap filters reject first
            ];

            if (incomingTxFilter is not null)
            {
                postHashFilters.Add(incomingTxFilter);
            }

            postHashFilters.Add(new DeployedCodeFilter(chainHeadInfoProvider.ReadOnlyStateProvider, _specProvider));

            // EIP-8141: resolve and record the frame-tx payer, rejecting provably-payerless prefixes.
            // Runs last so only otherwise-admissible frame txs are resolved.
            postHashFilters.Add(new FrameTxPayerFilter(chainHeadInfoProvider.ReadOnlyStateProvider, _logger));

            // EIP-8141: simulate the validation prefix of opaque (RequiresSimulation) frame txs to
            // resolve their payer and enforce the trace/opcode rules; runs after FrameTxPayerFilter
            // so the natively-resolved fast path bypasses it. Optional: when unwired, opaque frame
            // txs stay deferred as in Phase 1.
            postHashFilters.Add(new FrameTxSimulationFilter(chainHeadInfoProvider.ReadOnlyStateProvider, frameTxPrefixSimulator, _logger));

            // EIP-8141: bound each resolved payer's summed pending exposure to its balance; runs
            // after payer resolution/simulation, which records the payer this gate reads.
            postHashFilters.Add(new FrameTxPayerExposureFilter(_specProvider, chainHeadInfoProvider.ReadOnlyStateProvider, _payerExposure, _logger));

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
            IndexFrameTxDependencies(args.Value);
        }

        private void OnRemovedTx(object? sender, SortedPool<ValueHash256, Transaction, AddressAsKey>.SortedPoolRemovedEventArgs args)
        {
            RemovePendingDelegations(args.Value);
            if (HasExpiryDeadline(args.Value)) Interlocked.Decrement(ref _expiringFrameTxCount);
            ReleasePayerExposure(args.Value);
            if (args.Value.SupportsFrames) _frameDependencies.Remove(args.Value.Hash!.ValueHash256);
        }

        /// <summary>
        /// Records the chain-head accounts a pooled frame transaction's validation prefix depends on.
        /// </summary>
        /// <remarks>
        /// EIP-8141 "Direct Evaluation of Protocol-Defined Frames" names the sender, the payer and the
        /// expiry verifier as that set. Helper contracts an opaque prefix reaches through <c>CALL*</c> are
        /// not indexed yet, so a code change at one does not trigger revalidation (EIP8141-GAP).
        /// </remarks>
        private void IndexFrameTxDependencies(Transaction tx)
        {
            if (!tx.SupportsFrames) return;

            bool hasDistinctPayer = tx.PayerAddress is not null && tx.PayerAddress != tx.SenderAddress;
            bool hasExpiry = HasExpiryDeadline(tx);
            // A delegated sender runs the delegate's code, so that account is a dependency too; the sender's
            // own code hash only pins the designation.
            Address? delegated = DelegationTargetOf(tx.SenderAddress!);
            AddressAsKey[] accounts = new AddressAsKey[1 + (hasDistinctPayer ? 1 : 0) + (delegated is not null ? 1 : 0) + (hasExpiry ? 1 : 0)];
            int next = 0;
            accounts[next++] = tx.SenderAddress!;
            if (hasDistinctPayer) accounts[next++] = tx.PayerAddress!;
            if (delegated is not null) accounts[next++] = delegated;
            if (hasExpiry) accounts[next] = Eip8141Constants.ExpiryVerifierAddress;

            _frameDependencies.Set(tx.Hash!.ValueHash256, accounts);
        }

        private static bool HasExpiryDeadline(Transaction tx) => tx.SupportsFrames && FrameTxValidation.TryGetExpiryDeadline(tx, out _);

        /// <summary>The address an EIP-7702 designation at <paramref name="address"/> points at, or <c>null</c>.</summary>
        private Address? DelegationTargetOf(Address address)
        {
            // Read through the pool's account cache, as every other sender read here does, and gate on the
            // account carrying code so the overwhelmingly common codeless sender never loads code.
            if (!_accounts.TryGetAccount(address, out AccountStruct account) || !account.HasCode) return null;

            ReadOnlySpan<byte> code = _headInfo.ReadOnlyStateProvider.GetCode(address);
            return Eip7702Constants.IsDelegatedCode(code)
                ? new Address(code[Eip7702Constants.DelegationHeader.Length..])
                : null;
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
                    _newHeadLock.EnterWriteLock();
                    try
                    {
                        ArrayPoolList<AddressAsKey>? accountChanges = args.Block.AccountChanges;
                        // A reorg or a non-sequential block reports its own changes but not what the
                        // abandoned branch reverted, so the list does not describe everything that moved.
                        bool changeListIsComplete = args.PreviousBlock is null && CanUseCache(args.Block, accountChanges);
                        if (!changeListIsComplete)
                        {
                            // Non-sequential block or reorganization detected, reset cache
                            _accountCache.Reset();
                        }
                        else
                        {
                            // Sequential block, just remove changed accounts from cache
                            _accountCache.RemoveAccounts(accountChanges!);
                        }

                        // Collected before the change list is disposed; consumed after included and expired
                        // transactions have left the pool.
                        CollectFrameTxsToRevalidate(changeListIsComplete ? accountChanges : null);
                        DisposeBlockAccountChanges(args.Block);

                        _lastBlockNumber = args.Block.Number;
                        _lastBlockHash = args.Block.Hash;

                        ReAddReorganisedTransactions(args.PreviousBlock);
                        RemoveProcessedTransactions(args.Block);
                        // EIP-8141: for blob-carrying frame txs this evicts only under BlobsSupportMode.InMemory,
                        // since persistent storage's LightTransaction hard-codes TxType.Blob (SupportsFrames false).
                        RemoveExpiredFrameTransactions(args.Block);
                        RevalidateFrameTransactions(args.Block);

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
                        // Snapshot must be cleared inside the write lock so readers cannot
                        // regenerate it from a partially-updated _transactions collection.
                        // Placed in finally to guarantee clearing even if an exception occurs
                        // mid-update (otherwise readers could see a stale snapshot).
                        _transactionSnapshot = null;
                        _blobTransactionSnapshot = null;
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
                    if (tx.CarriesBlobs)
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
                    if (_logger.IsTrace) _logger.Trace($"Readded txs from reorged block {previousBlock.Number} (hash {previousBlock.Hash}) to blob pool");

                    _blobTxStorage.DeleteBlobTransactionsFromBlock(previousBlock.Number);
                }
            }
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

        /// <param name="completeAccountChanges">The head's changed accounts, or <c>null</c> when they do not describe everything that moved.</param>
        private void CollectFrameTxsToRevalidate(ArrayPoolList<AddressAsKey>? completeAccountChanges)
        {
            _frameTxsToRevalidate.Clear();
            if (_frameDependencies.Count == 0) return;

            if (completeAccountChanges is null) _frameDependencies.CollectAll(_frameTxsToRevalidate);
            else _frameDependencies.CollectAffected(completeAccountChanges, _frameTxsToRevalidate);
        }

        /// <summary>
        /// Re-resolves the validation prefix of the pending frame transactions whose tracked dependencies
        /// the new block touched, and evicts those that no longer satisfy the public mempool rules.
        /// </summary>
        /// <remarks>
        /// EIP-8141 "Revalidation". Only the dependency-affected subset is rechecked — revalidating the
        /// whole pool per head would be its own denial-of-service vector. Evicting here is the spec's
        /// "invalid against the current head first" eviction order: such transactions never compete for
        /// pool space in the first place. A simulation that fails on a resource bound rather than on the
        /// prefix leaves the transaction pending. The fork gate reads the incoming block's spec while pricing
        /// reads the head spec, matching <see cref="RemoveExpiredFrameTransactions"/>; they differ only for
        /// the one head that crosses a fork boundary.
        /// </remarks>
        private void RevalidateFrameTransactions(Block block)
        {
            if (_frameTxsToRevalidate.Count == 0 || !_specProvider.GetSpec(block.Header).IsEip8141Enabled)
            {
                _frameTxsToRevalidate.Clear();
                return;
            }

            IReleaseSpec headSpec = _specProvider.GetCurrentHeadSpec();
            IReadOnlyStateProvider state = _headInfo.ReadOnlyStateProvider;

            foreach (ValueHash256 hash in _frameTxsToRevalidate)
            {
                // A type-6 frame tx may carry blobs (blob pool) or not (normal pool), so check both.
                if ((!_transactions.TryGetValue(hash, out Transaction? tx) && !_blobTransactions.TryGetValue(hash, out tx))
                    || !tx.SupportsFrames)
                {
                    continue;
                }

                Metrics.FrameTxRevalidations++;
                if (!TryRevalidateFrameTransaction(tx, headSpec, state, out bool exposureReleased))
                {
                    // Cleared only when this pass already released the reservation, so the Removed handler
                    // releases it exactly once — an unreleased reservation would be permanent.
                    if (exposureReleased) tx.PayerAddress = null;
                    if (RemoveTransaction(tx.Hash))
                    {
                        EvictedPending?.Invoke(this, new TxEventArgs(tx));
                        // Unlike expiry, invalidity here is relative to this head and reverses (the payer
                        // refunds, a reorg restores the state), so the hash must stay resubmittable.
                        _hashCache.DeleteFromLongTerm(tx.Hash!);
                        Metrics.FrameTxRevalidationEvictions++;
                        Metrics.PendingTransactionsEvicted++;
                        if (_logger.IsTrace) _logger.Trace($"Evicted frame transaction {tx.Hash}, invalid against the new head.");
                    }
                }
            }

            _frameTxsToRevalidate.Clear();
        }

        /// <summary>Whether <paramref name="tx"/> still resolves a solvent payer against the new head, moving its reservation if the payer changed.</summary>
        /// <param name="exposureReleased">True when this call already released the reservation the transaction held.</param>
        /// <remarks>
        /// The solvency test compares the payer's whole pending exposure against its balance, so an
        /// over-committed payer sheds transactions one at a time: each eviction releases its reservation,
        /// and the rest of the sweep re-tests against the reduced total, leaving only the surplus dropped.
        /// <em>Which</em> of that payer's transactions survive follows index iteration order, not the spec's
        /// nearest-expiry-then-lowest-fee order.
        /// A transaction that stays pending is re-indexed: both the payer and the sender's delegation target
        /// are head-state snapshots, so either can move without the other.
        /// </remarks>
        private bool TryRevalidateFrameTransaction(Transaction tx, IReleaseSpec headSpec, IReadOnlyStateProvider state, out bool exposureReleased)
        {
            bool stillValid = ResolveFrameTxAgainstHead(tx, headSpec, state, out exposureReleased);
            if (stillValid) IndexFrameTxDependencies(tx);
            return stillValid;
        }

        private bool ResolveFrameTxAgainstHead(Transaction tx, IReleaseSpec headSpec, IReadOnlyStateProvider state, out bool exposureReleased)
        {
            exposureReleased = false;
            if (!FrameTxValidation.TryCalculateMaxCost(tx, headSpec, out UInt256 maxCost)) return false;

            // Matches TxFilteringState: a never-seen sender must read back as code-free, not zero-hashed.
            if (!_accounts.TryGetAccount(tx.SenderAddress!, out AccountStruct senderAccount)) senderAccount = AccountStruct.TotallyEmpty;

            Address? payer;
            FrameTxPayerResolution resolution = FrameTxPayerResolver.Resolve(tx, state, senderAccount);
            switch (resolution.Outcome)
            {
                case FrameTxPayerOutcome.NoPayer:
                    return false;
                case FrameTxPayerOutcome.Resolved:
                    payer = resolution.Payer;
                    break;
                default:
                    // Opaque: with no simulator wired the prefix stays unresolved, exactly as at admission.
                    if (_frameTxPrefixSimulator is null) return true;
                    FrameTxSimulationResult simulated = _frameTxPrefixSimulator.Simulate(tx, _cts.Token);
                    if (!simulated.Accepted) return simulated.Indeterminate;
                    payer = simulated.Payer;
                    break;
            }

            Address? previousPayer = tx.PayerAddress;
            if (previousPayer == payer)
            {
                // Same payer: only its balance can have invalidated the bound.
                return payer is null || _payerExposure.GetReserved(payer) <= BalanceOf(state, payer);
            }

            if (previousPayer is not null) _payerExposure.Subtract(previousPayer, maxCost);
            exposureReleased = true;
            tx.PayerAddress = payer;
            return payer is null || _payerExposure.TryReserve(payer, maxCost, BalanceOf(state, payer), out _);
        }

        private static UInt256 BalanceOf(IReadOnlyStateProvider state, Address address) =>
            state.TryGetAccount(address, out AccountStruct account) ? account.Balance : UInt256.Zero;

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
                    Transaction[] txSnapshot = _transactionSnapshot ??= _transactions.GetSnapshot();
                    Transaction[] blobTxSnapshot = _blobTransactionSnapshot ??= _blobTransactions.GetSnapshot();
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

                // Reset snapshots
                _transactionSnapshot = null;
                _blobTransactionSnapshot = null;
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
                // Snapshot must be cleared inside the read lock so a concurrent reader
                // cannot cache a snapshot taken between AddCore completing and the
                // null-assignment (which would be missing the just-added tx).
                if (accepted)
                {
                    if (tx.CarriesBlobs)
                        _blobTransactionSnapshot = null;
                    else
                        _transactionSnapshot = null;
                }
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
            // The exposure filter already reserved this frame tx's max cost; the reservation is settled
            // once the pool owns it (or a Removed event has released it). Anything throwing before that
            // would leak it permanently, hence the finally.
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
                // Past here the pool owns the reservation, or a self-eviction Removed already released it.
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
                    // it means it was added and immediately evicted - pool was full of better txs.
                    // A frame tx kept only by the persistent broadcaster below then contributes nothing to
                    // its payer's exposure: the broadcaster has no Removed hook, so holding one would leak.
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
            if (!tx.SupportsFrames || tx.PayerAddress is null) return;

            if (FrameTxValidation.TryCalculateMaxCost(tx, _specProvider.GetCurrentHeadSpec(), out UInt256 maxCost))
            {
                _payerExposure.Subtract(tx.PayerAddress, maxCost);
            }
            else if (_logger.IsWarn)
            {
                // Unreachable while pricing is deterministic; a phantom reservation would otherwise be silent.
                _logger.Warn($"Could not price frame transaction {tx.Hash} to release payer {tx.PayerAddress} exposure.");
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
