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
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const int RevalidationAbandonmentWarningThreshold = 3;
        private const int MarkerPublicationDeferralWarningThreshold = 3;

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
        private bool _specChangeMarkerUnpublished;
        private readonly DelegationCache _pendingDelegations = new();
        private readonly PayerExposureCache _payerExposure = new();
        private readonly PendingPaymasterCache _pendingPaymasters = new();
        private readonly FrameTxDependencyIndex _frameDependencies = new();
        private readonly HashSet<ValueHash256> _frameTxsToRevalidate = [];
        private readonly HashSet<ValueHash256> _frameTxsDeferredToNextHead = [];

        // Candidate filter for the shed pass, calibrated on a 12s slot; on a faster chain it simply admits
        // more transactions to the deadline order, which is the order the spec asks for anyway.
        private const ulong ExpiryShedHorizonSeconds = 24;
        private readonly IFrameTxPrefixSimulator? _frameTxPrefixSimulator;
        private readonly HashSet<Hash256> _forkInvalidatedHashes = [];
        private IReleaseSpec? _forkInvalidatedSpec;

        private readonly ILogger _logger;

        private readonly Channel<HeadChange> _headBlocksChannel = Channel.CreateUnbounded<HeadChange>(new UnboundedChannelOptions() { SingleReader = true, SingleWriter = true });
        private readonly Channel<bool> _revalidationChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        private readonly ReaderWriterLockSlim _newHeadLock = new(LockRecursionPolicy.SupportsRecursion);
        private readonly Lock _forkStateLock = new();
        private readonly LatestRevalidationRequest _latestRevalidationRequest = new();
        // Publish the spec and its epoch through one reference so readers cannot combine different observations.
        private HeadSpecObservation? _headSpecObservation;
        private long _headGeneration;
        private long _forkStateVersion;
        private int _consecutiveRevalidationAbandonments;
        private int _consecutiveMarkerPublicationDeferrals;

        private readonly UpdateGroupDelegate _updateBucket;
        private readonly UpdateGroupDelegate _updateBucketAdded;
        private readonly Task _headProcessing;
        private readonly Task _revalidationProcessing;
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
        /// A specification is published after both pools have been walked, when both pools are empty, or when
        /// persistent storage has a matching validation marker. <see cref="AddCore"/> runs under the head read
        /// lock and may only clear the field. <see cref="_forkStateVersion"/> brackets both operations so block
        /// production can detect a concurrent state change without waiting for head processing.
        /// </remarks>
        private IReleaseSpec? _validatedSpec;

        private bool _isDisposed;
        private long _pendingTransactionsAdded = 0;

        // Lets the per-head expiry pass skip the pool walk entirely when nothing can expire. Maintained by the
        // Inserted/Removed handlers under Interlocked, so readers need only Volatile.Read for visibility.
        private int _expiringFrameTxCount;

#if DEBUG
        // Bumped before the bookkeeping either side of a mutation moves, so a half-applied mutation cannot read as drift.
        private int _poolMutations;
#endif

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
        /// <param name="frameTxPrefixSimulator">Optional EIP-8141 opaque-prefix simulator; unwired on chains without frame transactions.</param>
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
            bool thereIsPriorityContract = false,
            IFrameTxPrefixSimulator? frameTxPrefixSimulator = null)
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
            _frameTxPrefixSimulator = frameTxPrefixSimulator;
            _accounts = _accountCache = new AccountCache(_headInfo.ReadOnlyStateProvider);
            _specProvider = _headInfo.SpecProvider;
            ObserveHeadSpec(_specProvider.GetCurrentHeadSpec());
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
                ? new PersistentBlobTxDistinctSortedPool(
                    blobTxStorage,
                    _txPoolConfig,
                    comparer,
                    logManager,
                    TimeProvider.System,
                    RequestCurrentSpecRevalidation)
                : new BlobTxDistinctSortedPool(txPoolConfig.BlobsSupport == BlobsSupportMode.InMemory ? _txPoolConfig.InMemoryBlobPoolSize : 0, comparer, logManager);
            // Records restored inside the pool's constructor predate the handlers below, so the count and both
            // ledgers are seeded before subscribing: UpdatePool evicts during startup, and a removal must
            // release against a ledger that already covers what it removes.
            if (_blobTransactions.Count > 0)
            {
                foreach (Transaction restored in _blobTransactions.GetSnapshot())
                {
                    if (HasExpiryDeadline(restored)) _expiringFrameTxCount++;
                    // EIP-8141: the bound is summed over the pending set, so a record that survived the restart
                    // has to keep counting against its payer. Restored, not re-gated: the reservation was
                    // granted at admission, and refusing it now would leave a record no removal releases.
                    // The same predicate the release reads, so the two ends of a ledger entry cannot drift.
                    if (TryGetPayerReservation(restored, out Address? payer, out UInt256 reserved))
                    {
                        _payerExposure.Restore(payer, reserved);
                    }

                    // Re-taken rather than re-gated for the same reason, and through the key the release reads.
                    if (PendingPaymasterCache.KeyFor(restored) is Address paymaster)
                    {
                        _pendingPaymasters.Reserve(paymaster);
                    }
                }
            }

            // EIP-8141: blob-carrying frame txs live in the blob pool, so it needs the same insert/removal bookkeeping.
            _blobTransactions.Inserted += OnInsertedTx;
            _blobTransactions.Removed += OnRemovedTx;

            UpdateBucketsWithoutRevalidation();
            InitializeValidatedSpec();

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
                new MalformedTxFilter(validator, _specChangeTxValidator, ecdsa, _logger),
                // after MalformedTxFilter, before anything prices the transaction: a locally built frame tx
                // skips the decoder that measures these, and would be priced as if the fields were free
                new FrameTxCalldataStatsFilter(),
                new FrameTxMisplacedExpiryFrameFilter(_logger), // before ExpiredFrameTxFilter: leaves the deadline readable from the leading frame alone
                new ExpiredFrameTxFilter(chainHeadInfoProvider, _logger), // after MalformedTxFilter: reads the deadline from an already well-formed frame
                new FrameTxVerifyGasFilter(txPoolConfig, _logger), // after MalformedTxFilter: reads gas limits from an already well-formed frame list
                new FrameTxPayerlessFilter(_logger), // before FrameTxSignatureFilter: a structural payerless verdict needs no signature work
                new FrameTxVerifyAfterPrefixFilter(_logger), // after MalformedTxFilter: matches the prefix grammar against an already recovered sender

                new TxTypeTxFilter(_transactions,
                    _blobTransactions), // has to be after MalformedTxFilter as it uses the recovered sender
                new BalanceZeroFilter(thereIsPriorityContract, _logger),
                new BalanceTooLowFilter(_transactions, _blobTransactions, _logger),
                new LowNonceFilter(_logger), // has to be after MalformedTxFilter as it uses the recovered sender
                new FutureNonceFilter(txPoolConfig),
                new GapNonceFilter(_transactions, _blobTransactions, _logger),
                new KeyedNonceFilter(chainHeadInfoProvider.ReadOnlyStateProvider), // the three above skip keyed sets, this one owns them
                new RecoverAuthorityFilter(ecdsa),
                new DelegatedAccountFilter(_transactions, _blobTransactions, chainHeadInfoProvider.ReadOnlyStateProvider, _pendingDelegations),
                new FrameTxSignatureFilter(_specProvider, ecdsa, _logger), // last: elliptic-curve recovery per signature, up to the decoder's 1024, so let the cheap filters reject first
            ];

            if (incomingTxFilters is not null)
            {
                postHashFilters.AddRange(incomingTxFilters);
            }

            postHashFilters.Add(new DeployedCodeFilter(chainHeadInfoProvider.ReadOnlyStateProvider));
            postHashFilters.Add(new BlobProofsTxFilter());

            // EIP-8141: cap the pending frame txs one non-canonical paymaster may sponsor. After the filters
            // that prove a transaction garbage, so taking a sponsor's slot needs a valid one; before the
            // simulation, which is the per-sponsor work the cap exists to bound.
            postHashFilters.Add(new FrameTxPaymasterFilter(chainHeadInfoProvider.ReadOnlyStateProvider, _transactions, _blobTransactions, _pendingPaymasters, _logger));

            // EIP-8141: resolve last, so only otherwise-admissible frame txs are resolved.
            postHashFilters.Add(new FrameTxPayerFilter(_logger));

            // EIP-8141: after FrameTxPayerFilter, so the natively-resolved fast path bypasses it.
            postHashFilters.Add(new FrameTxSimulationFilter(frameTxPrefixSimulator, _logger));

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

            _revalidationProcessing = ProcessRevalidations();
            bool specValidatedForHead = ReferenceEquals(Volatile.Read(ref _validatedSpec), _specProvider.GetCurrentHeadSpec());
            if (RequiresRevalidation(specValidatedForHead))
            {
                RequestRevalidation(Volatile.Read(ref _headGeneration));
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
        /// <remarks>An EIP-8250 keyed set does not use the account nonce, so readiness is per-key currency instead.</remarks>
        private bool IsNonceReady(Transaction tx, Address sender) =>
            KeyedNonceManager.UsesKeyedNonce(tx)
                ? IsKeyedNonceCurrent(tx)
                : tx.Nonce == _accounts.GetNonce(sender);

        public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender() =>
            _blobTransactions.GetBucketSnapshot();

        public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender(bool filterToReadyTx, UInt256 baseFee = default) =>
            _blobTransactions.GetBucketSnapshot(filterToReadyTx
                ? data => data.first.CanPayBaseFee(baseFee) && IsNonceReady(data.first, data.key)
                : null);

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

        public bool TryGetPendingBlobCellMask(Hash256 hash, out BlobCellMask availableMask)
            => _blobTransactions.TryGetAvailableCellMask(hash, out availableMask);

        public bool TryGetPendingBlobCellMetadata(
            Hash256 hash,
            out BlobCellMask availableMask,
            out int blobCount,
            out int materializationWork) =>
            _blobTransactions.TryGetAvailableCellMetadata(
                hash,
                out availableMask,
                out blobCount,
                out materializationWork);

        public bool TryGetBlobCells(Hash256 hash, BlobCellMask requestedMask, out BlobCellMask availableMask, [NotNullWhen(true)] out byte[][]? cells)
            => _blobTransactions.TryGetCells(hash, requestedMask, out availableMask, out cells);

        public bool TryGetBlobCellsAndProofsV1(byte[] blobVersionedHash, BlobCellMask requestedMask, out BlobCellMask availableMask, [NotNullWhen(true)] out byte[][]? cells, [NotNullWhen(true)] out byte[][]? proofs)
            => _blobTransactions.TryGetBlobCellsAndProofsV1(blobVersionedHash, requestedMask, out availableMask, out cells, out proofs);

        public bool TryMergeBlobCells(Hash256 hash, BlobCellMask cellMask, byte[][] cells) =>
            MergeBlobCells(hash, cellMask, cells) == BlobCellMergeResult.Accepted;

        public BlobCellMergeResult MergeBlobCells(Hash256 hash, BlobCellMask cellMask, byte[][] cells)
        {
            BlobCellMergeResult result = _blobTransactions.MergeCells(hash, cellMask, cells);
            if (result != BlobCellMergeResult.Accepted)
            {
                return result;
            }

            if (_blobTransactions.TryGetValue(hash, out Transaction? transaction))
            {
                _broadcaster.Broadcast(transaction, isPersistent: false);
            }

            return BlobCellMergeResult.Accepted;
        }

        private void OnInsertedTx(object? sender, SortedPool<ValueHash256, Transaction, AddressAsKey>.SortedPoolEventArgs args)
        {
            TrackPoolMutation();
            AddPendingDelegations(args.Value);
            if (HasExpiryDeadline(args.Value)) Interlocked.Increment(ref _expiringFrameTxCount);
            IndexFrameTxDependencies(args.Value);
        }

        private void OnRemovedTx(object? sender, SortedPool<ValueHash256, Transaction, AddressAsKey>.SortedPoolRemovedEventArgs args)
        {
            TrackPoolMutation();
            RemovePendingDelegations(args.Value);
            if (HasExpiryDeadline(args.Value))
            {
                int remaining = Interlocked.Decrement(ref _expiringFrameTxCount);
                AssertExpiringFrameTxCountNotNegative(remaining);
            }

            ReleaseFrameTxReservations(args.Value);
            if (args.Value.SupportsFrames) _frameDependencies.Remove(args.Value.Hash!.ValueHash256);
        }

        /// <summary>
        /// Records the chain-head accounts a pooled frame transaction's validation prefix depends on.
        /// </summary>
        /// <remarks>
        /// EIP-8141 "Direct Evaluation of Protocol-Defined Frames" names the sender, the payer and the expiry
        /// verifier as that set. The expiry verifier is deliberately left out: <see cref="Block.AccountChanges"/>
        /// is a touched set, not a write set, so every block running an expiry-bearing frame transaction names
        /// it and would collect the whole expiring population — the sweep this index exists to avoid. Its
        /// predeployed code never changes, so the entry has no true positives, and the deadline it stands for
        /// is swept by <see cref="RemoveExpiredFrameTransactions"/> instead.
        /// Two kinds of dependency sit outside the set (EIP8141-GAP): helper contracts an opaque prefix reaches
        /// through <c>CALL*</c>, so a code change at one does not trigger revalidation; and block context it
        /// reads (<c>TIMESTAMP</c>, <c>NUMBER</c>), which no change list can describe.
        /// </remarks>
        /// <param name="resolvedPayer">A payer the sweep resolved but did not record, so it is still tracked.</param>
        private void IndexFrameTxDependencies(Transaction tx, Address? resolvedPayer = null)
        {
            // Under persistent blob storage the pool holds a frameless light record. There is no prefix left
            // to re-resolve, so indexing it would only queue a revalidation that must reject it.
            if (!tx.SupportsFrames || tx.Frames is null) return;

            Address? payer = tx.PayerAddress ?? resolvedPayer;
            bool hasDistinctPayer = payer is not null && payer != tx.SenderAddress;
            // A delegated sender runs the delegate's code, so that account is a dependency too; the sender's
            // own code hash only pins the designation.
            Address? delegated = DelegationTargetOf(tx.SenderAddress!);
            AddressAsKey[] accounts = new AddressAsKey[1 + (hasDistinctPayer ? 1 : 0) + (delegated is not null ? 1 : 0)];
            int next = 0;
            accounts[next++] = tx.SenderAddress!;
            if (hasDistinctPayer) accounts[next++] = payer!;
            if (delegated is not null) accounts[next] = delegated;

            _frameDependencies.Set(tx.Hash!.ValueHash256, accounts);
        }

        private static bool HasExpiryDeadline(Transaction tx) => tx.SupportsFrames && FrameTxValidation.TryGetExpiryDeadline(tx, out _);

        [Conditional("DEBUG")]
        private void TrackPoolMutation()
        {
#if DEBUG
            Interlocked.Increment(ref _poolMutations);
#endif
        }

        // A negative count arms the expiry sweep's zero-count fast path for good. Judge the decrement's own result:
        // removal holds no head lock, so re-reading the field lets a concurrent insert hide the excursion.
        [Conditional("DEBUG")]
        private static void AssertExpiringFrameTxCountNotNegative(int remaining) =>
            Debug.Assert(remaining >= 0, "Expiring frame transaction count went negative.");

        // A missed release or decrement persists for the life of the pool, locking the payer out or disarming the
        // expiry sweep. Per head rather than per operation: the walk is O(pool size) and insert and removal are hot.
        [Conditional("DEBUG")]
        private void AssertFrameTxBookkeeping()
        {
#if DEBUG
            for (int attempt = 0; attempt < 3; attempt++)
            {
                // Read before the snapshots, or the window reopens: the pool drops its snapshot cache before
                // raising Removed, so a walk that sees this bump rebuilds behind the handler that released.
                int mutations = Volatile.Read(ref _poolMutations);

                Dictionary<AddressAsKey, UInt256> pooledExposure = [];
                Dictionary<AddressAsKey, int> pooledPaymasters = [];
                int pooledExpiring = 0;
                AccumulateFrameTxBookkeeping(_transactions.GetSnapshot(), pooledExposure, pooledPaymasters, ref pooledExpiring);
                AccumulateFrameTxBookkeeping(_blobTransactions.GetSnapshot(), pooledExposure, pooledPaymasters, ref pooledExpiring);

                int recordedExpiring = Volatile.Read(ref _expiringFrameTxCount);
                List<KeyValuePair<AddressAsKey, UInt256>> recordedExposure = [.. _payerExposure.Reservations];
                List<KeyValuePair<AddressAsKey, int>> recordedPaymasters = [.. _pendingPaymasters.Counts];

                // RemoveTransaction and EvictTransaction run outside the head lock held here, so a mutation across
                // the reading means it was torn rather than the pool inconsistent.
                if (Volatile.Read(ref _poolMutations) != mutations) continue;

                Debug.Assert(recordedExpiring == pooledExpiring,
                    $"Expiring frame transaction count is {recordedExpiring}, but {pooledExpiring} pooled transactions carry a deadline.");
                Debug.Assert(recordedExposure.Count == pooledExposure.Count,
                    $"Payer exposure ledger holds {recordedExposure.Count} payers, but {pooledExposure.Count} are pooled.");
                Debug.Assert(recordedPaymasters.Count == pooledPaymasters.Count,
                    $"Paymaster cap ledger holds {recordedPaymasters.Count} paymasters, but {pooledPaymasters.Count} are pooled.");

                foreach (KeyValuePair<AddressAsKey, UInt256> reservation in recordedExposure)
                {
                    // Not Debug.Assert: its message argument is eager, and this one would format once per payer per head.
                    if (!pooledExposure.TryGetValue(reservation.Key, out UInt256 pooled) || pooled != reservation.Value)
                    {
                        Debug.Fail($"Payer {reservation.Key} holds {reservation.Value} reserved, but its pooled transactions total {pooled}.");
                    }
                }

                foreach (KeyValuePair<AddressAsKey, int> counted in recordedPaymasters)
                {
                    if (!pooledPaymasters.TryGetValue(counted.Key, out int pooled) || pooled != counted.Value)
                    {
                        Debug.Fail($"Paymaster {counted.Key} counts {counted.Value} pending, but {pooled} pooled transactions name it.");
                    }
                }

                return;
            }

            if (_logger.IsTrace) _logger.Trace("Frame transaction bookkeeping check skipped: every reading was torn by a concurrent pool mutation.");
#endif
        }

#if DEBUG
        // A restored record's payer and paymaster are both persisted, so it is inside this check's reach.
        private static void AccumulateFrameTxBookkeeping(
            Transaction[] snapshot, Dictionary<AddressAsKey, UInt256> exposure, Dictionary<AddressAsKey, int> paymasters, ref int expiring)
        {
            foreach (Transaction tx in snapshot)
            {
                if (HasExpiryDeadline(tx)) expiring++;

                if (PendingPaymasterCache.KeyFor(tx) is Address paymaster)
                {
                    paymasters[paymaster] = paymasters.TryGetValue(paymaster, out int pending) ? pending + 1 : 1;
                }

                // A zero cost is never recorded by admission, so it must not be expected back either.
                if (!TryGetPayerReservation(tx, out Address? payer, out UInt256 maxCost) || maxCost.IsZero)
                {
                    continue;
                }

                exposure[payer] = exposure.TryGetValue(payer, out UInt256 running) ? running + maxCost : maxCost;
            }
        }
#endif

        /// <summary>The address an EIP-7702 designation at <paramref name="address"/> points at, or <c>null</c>.</summary>
        private Address? DelegationTargetOf(Address address)
        {
            // Also reached from the head thread by the revalidation sweep, so it takes no pool lock: gated on
            // the account carrying code at all, which keeps a codeless sender to one cached read.
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

        private long ObserveHeadSpec(IReleaseSpec spec)
        {
            HeadSpecObservation? observation = Volatile.Read(ref _headSpecObservation);
            while (!ReferenceEquals(observation?.Spec, spec))
            {
                HeadSpecObservation updated = new(spec, (observation?.Generation ?? 0) + 1);
                HeadSpecObservation? published = Interlocked.CompareExchange(
                    ref _headSpecObservation,
                    updated,
                    observation);

                if (ReferenceEquals(published, observation))
                {
                    return updated.Generation;
                }

                observation = published;
            }

            return observation.Generation;
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
                while (_headBlocksChannel.Reader.TryRead(out HeadChange headChange))
                {
                    BlockReplacementEventArgs args = headChange.Args;
                    try
                    {
                        bool bucketsUpdated;
                        bool revalidationRequired;
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
                            RemoveExpiredFrameTransactions(args.Block);
                            RevalidateFrameTransactions(args.Block);

                            if (!_headInfo.IsSyncing || AcceptTxWhenNotSynced || args.PreviousBlock is not null)
                            {
                                _hashCache.ClearCurrentBlockCache();
                            }

                            bucketsUpdated = ReferenceEquals(Volatile.Read(ref _validatedSpec), _specProvider.GetCurrentHeadSpec());
                            revalidationRequired = RequiresRevalidation(bucketsUpdated);
                            if (bucketsUpdated)
                            {
                                UpdateBucketsWithoutRevalidation();
                            }

                            // After the bucket update, which drops what the new head invalidated: shedding answers
                            // capacity pressure, so it must read the pressure that actually remains.
                            ShedNearlyExpiredFrameTransactions(args.Block);
                            AssertFrameTxBookkeeping();
                        }
                        finally
                        {
                            _newHeadLock.ExitWriteLock();
                        }

                        if (revalidationRequired)
                        {
                            RequestRevalidation(headChange.Generation);
                        }

                        // Subscribers can re-enter the pool, so invoke them after releasing _newHeadLock.
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

        private void RequestRevalidation(long generation)
        {
            _latestRevalidationRequest.Update(generation);
            _revalidationChannel.Writer.TryWrite(true);
        }

        private void RequestCurrentSpecRevalidation() => RequestRevalidation(Volatile.Read(ref _headGeneration));

        private bool RequiresRevalidation(bool specValidatedForHead) =>
            !specValidatedForHead
            || Volatile.Read(ref _specChangeMarkerUnpublished);

        private async Task ProcessRevalidations()
        {
            try
            {
                await Task.Run(ProcessRevalidationLoop);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("Transaction pool revalidation failed.", ex);
            }
        }

        private async Task ProcessRevalidationLoop()
        {
            while (await _revalidationChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_revalidationChannel.Reader.TryRead(out _)) { }

                long generation = _latestRevalidationRequest.Generation;

                try
                {
                    if (!TryRevalidateCurrentSpec(generation))
                    {
                        _newHeadLock.EnterWriteLock();
                        try
                        {
                            if (!_cts.IsCancellationRequested
                                && !ReferenceEquals(Volatile.Read(ref _validatedSpec), _specProvider.GetCurrentHeadSpec()))
                            {
                                UpdateBucketsWithoutRevalidation();
                            }
                        }
                        finally
                        {
                            _newHeadLock.ExitWriteLock();
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsWarn) _logger.Warn($"Transaction pool failed to update after an unsuccessful revalidation with exception {ex}");
                }
            }
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

        /// <summary>Drops pending EIP-8141 frame transactions whose expiry deadline has passed as of the new head.</summary>
        /// <remarks>The predeploy reverts only once <c>block.timestamp &gt; deadline</c>, so the comparison is strict here too.</remarks>
        // EIP8141-GAP: linear scan; a deadline-ordered index is deferred to the scalable eviction layer.
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
                        // Unlike a capacity eviction, the hash is deliberately left in _hashCache: an expired
                        // frame tx can never be included, so it must not re-enter the pool.
                        EvictedPending?.Invoke(this, new TxEventArgs(tx));
                        Metrics.PendingTransactionsEvicted++;
                        if (_logger.IsTrace) _logger.Trace($"Evicted expired frame transaction {tx.Hash} (deadline {deadline} < head timestamp {timestamp}).");
                    }
                }
            }
        }

        /// <summary>
        /// Frees one slot in each pool at capacity by shedding the frame transaction nearest its deadline,
        /// lowest effective priority fee first among equals.
        /// </summary>
        /// <remarks>
        /// EIP-8141 "Replacement and Eviction" orders eviction as invalid-against-head, then nearest expiry,
        /// then lowest effective priority fee; the first tier is <see cref="RevalidateFrameTransactions"/>.
        /// Applying the deadline order across the whole pool needs a deadline-ordered index inside the pool
        /// (EIP8141-GAP), so this removes one transaction per pool per head, and only within
        /// <see cref="ExpiryShedHorizonSeconds"/> of the deadline. Shedding a sender's current-nonce
        /// transaction leaves a nonce gap, so the next head's bucket update drops that sender's remainder.
        /// </remarks>
        private void ShedNearlyExpiredFrameTransactions(Block block)
        {
            if (Volatile.Read(ref _expiringFrameTxCount) == 0
                || !_specProvider.GetSpec(block.Header).IsEip8141Enabled)
            {
                return;
            }

            // Saturating: a head timestamp near ulong.MaxValue would otherwise trap in a checked build.
            ulong horizon = block.Timestamp > ulong.MaxValue - ExpiryShedHorizonSeconds
                ? ulong.MaxValue
                : block.Timestamp + ExpiryShedHorizonSeconds;
            ShedNearlyExpiredFrameTransactions(_transactions, horizon);
            ShedNearlyExpiredFrameTransactions(_blobTransactions, horizon);
        }

        private void ShedNearlyExpiredFrameTransactions(TxDistinctSortedPool pool, ulong horizon)
        {
            if (!pool.IsFull()) return;

            UInt256 baseFee = _headInfo.CurrentBaseFee;
            bool eip1559Enabled = _specProvider.GetCurrentHeadSpec().IsEip1559Enabled;
            Transaction[] snapshot = pool.GetSnapshot();

            // One removal clears IsFull, so only the minimum is ever needed: a linear scan, not a sort.
            Transaction? candidate = null;
            ulong bestDeadline = 0;
            UInt256 bestFee = default;
            for (int i = 0; i < snapshot.Length; i++)
            {
                Transaction tx = snapshot[i];
                if (!tx.SupportsFrames
                    || !FrameTxValidation.TryGetExpiryDeadline(tx, out ulong deadline)
                    || deadline > horizon)
                {
                    continue;
                }

                UInt256 fee = tx.CalculateMaxPriorityFeePerGas(eip1559Enabled, baseFee);
                if (candidate is null || deadline < bestDeadline || (deadline == bestDeadline && fee < bestFee))
                {
                    (candidate, bestDeadline, bestFee) = (tx, deadline, fee);
                }
            }

            if (candidate is null || !RemoveTransaction(candidate.Hash)) return;

            EvictedPending?.Invoke(this, new TxEventArgs(candidate));
            // Capacity pressure decided this, not expiry: the transaction is still includable, and the
            // pressure reverses within a block, so the hash must stay resubmittable.
            _hashCache.DeleteFromLongTerm(candidate.Hash!);
            Metrics.PendingTransactionsEvicted++;
            Interlocked.Increment(ref Metrics.FrameTxExpiryShedEvictions);
            if (_logger.IsTrace) _logger.Trace($"Shed nearly-expired frame transaction {candidate.Hash} to relieve pool pressure.");
        }

        /// <param name="completeAccountChanges">The head's changed accounts, or <c>null</c> when they do not describe everything that moved.</param>
        private void CollectFrameTxsToRevalidate(ArrayPoolList<AddressAsKey>? completeAccountChanges)
        {
            _frameTxsToRevalidate.Clear();
            if (_frameDependencies.Count > 0)
            {
                if (completeAccountChanges is null) _frameDependencies.CollectAll(_frameTxsToRevalidate);
                else _frameDependencies.CollectAffected(completeAccountChanges, _frameTxsToRevalidate);
            }

            // Carried from the previous head: a bound this node spent judged nothing, and a one-off change
            // leaves no later change list that would name the transaction's dependencies again. Unioned last,
            // so a saturated budget spends on this head's changes before the previous head's backlog.
            _frameTxsToRevalidate.UnionWith(_frameTxsDeferredToNextHead);
            _frameTxsDeferredToNextHead.Clear();
        }

        /// <summary>
        /// Re-resolves the validation prefix of the pending frame transactions whose tracked dependencies
        /// the new block touched, and evicts those that no longer satisfy the public mempool rules.
        /// </summary>
        /// <remarks>
        /// EIP-8141 "Revalidation". Only the dependency-affected subset is rechecked, plus whatever the
        /// previous head's admission bounds left unjudged — revalidating the
        /// whole pool per head would be its own denial-of-service vector, and it is why caching a simulation
        /// result against its dependency set would add nothing: a re-simulated prefix has already moved.
        /// Evicting here is the spec's
        /// "invalid against the current head first" eviction order: such transactions never compete for
        /// pool space in the first place. A simulation that fails on a resource bound rather than on the
        /// prefix leaves the transaction pending. The fork gate reads the incoming block's spec, matching
        /// <see cref="RemoveExpiredFrameTransactions"/>. Nothing here re-prices or moves a reservation: the
        /// pooled record is left as admission wrote it, so removal releases exactly what admission took.
        /// </remarks>
        private void RevalidateFrameTransactions(Block block)
        {
            IReleaseSpec spec = _specProvider.GetSpec(block.Header);
            if (_frameTxsToRevalidate.Count == 0 || !spec.IsEip8141Enabled)
            {
                _frameTxsToRevalidate.Clear();
                return;
            }

            IReadOnlyStateProvider state = _headInfo.ReadOnlyStateProvider;

            foreach (ValueHash256 hash in _frameTxsToRevalidate)
            {
                // A type-6 frame tx may carry blobs (blob pool) or not (normal pool), so check both.
                if ((!_transactions.TryGetValue(hash, out Transaction? tx) && !_blobTransactions.TryGetValue(hash, out tx))
                    || !tx.SupportsFrames
                    || tx.Frames is null)
                {
                    continue;
                }

                Interlocked.Increment(ref Metrics.FrameTxRevalidations);
                if (!TryRevalidateFrameTransaction(tx, state))
                {
                    // The record is untouched, so the Removed handler releases exactly what admission took.
                    if (RemoveTransaction(tx.Hash))
                    {
                        EvictedPending?.Invoke(this, new TxEventArgs(tx));
                        // Unlike expiry, invalidity here is relative to this head and reverses (the payer
                        // refunds, a reorg restores the state), so the hash must stay resubmittable.
                        _hashCache.DeleteFromLongTerm(tx.Hash!);
                        Interlocked.Increment(ref Metrics.FrameTxRevalidationEvictions);
                        Metrics.PendingTransactionsEvicted++;
                        if (_logger.IsTrace) _logger.Trace($"Evicted frame transaction {tx.Hash}, invalid against the new head.");
                    }
                }
            }

            _frameTxsToRevalidate.Clear();
        }

        /// <summary>Whether <paramref name="tx"/> still resolves the solvent payer it was admitted against.</summary>
        /// <remarks>
        /// The solvency test compares the payer's whole pending exposure against its balance, so an
        /// over-committed payer sheds transactions one at a time: each eviction releases its reservation,
        /// and the rest of the sweep re-tests against the reduced total, leaving only the surplus dropped.
        /// <em>Which</em> of that payer's transactions survive follows index iteration order, not the spec's
        /// nearest-expiry-then-lowest-fee order.
        /// A transaction that stays pending is re-indexed for the sender's delegation target, a head-state
        /// snapshot that can move while the payer does not, since a payer that moves evicts instead.
        /// </remarks>
        private bool TryRevalidateFrameTransaction(Transaction tx, IReadOnlyStateProvider state)
        {
            bool stillValid = ResolveFrameTxAgainstHead(tx, state, out Address? resolvedPayer);
            if (stillValid) IndexFrameTxDependencies(tx, resolvedPayer);
            return stillValid;
        }

        private bool ResolveFrameTxAgainstHead(Transaction tx, IReadOnlyStateProvider state, out Address? resolvedPayer)
        {
            resolvedPayer = null;

            // Matches TxFilteringState: a never-seen sender must read back as code-free, not zero-hashed.
            if (!_accounts.TryGetAccount(tx.SenderAddress!, out AccountStruct senderAccount)) senderAccount = AccountStruct.TotallyEmpty;

            Address? payer;
            FrameTxPayerResolution resolution = FrameTxPayerResolver.Resolve(tx, senderAccount);
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
                    // validate_signature reads no state, so admission's verdict still holds and re-verifying
                    // would only spend the per-head simulation budget this pool rations.
                    FrameTxSimulationResult simulated = _frameTxPrefixSimulator.Simulate(tx, signaturesPreValidated: true, token: _cts.Token);
                    if (simulated.Outcome != FrameTxSimulationOutcome.Accepted)
                    {
                        // A node fault or an admission bound decides nothing, so the transaction stays
                        // pending — and stays queued, or a one-off change is never rechecked against a
                        // later head whose change list does not mention its dependencies. Only a bound this
                        // node spent: a prefix that trips its own wall clock would re-queue forever.
                        if (simulated.NodeBound)
                        {
                            _frameTxsDeferredToNextHead.Add(tx.Hash!.ValueHash256);
                            Interlocked.Increment(ref Metrics.FrameTxRevalidationsDeferred);
                        }

                        return simulated.Indeterminate;
                    }
                    payer = simulated.Payer;
                    break;
            }

            if (tx.PayerAddress == payer)
            {
                // Same payer: only its balance can have invalidated the bound.
                return payer is null || _payerExposure.GetReserved(payer) <= BalanceOf(state, payer);
            }

            // The payer moved, and it is never rewritten in place: RemoveTransaction runs from block production
            // and the network thread without the head lock, so a removal landing between the payer and exposure
            // writes would release the wrong figure from the wrong payer, and both errors are permanent.
            // Evict instead, so the reservation leaves through the Removed handler that took it.
            if (tx.PayerAddress is not null) return false;

            // Admitted while this node could not simulate, so it holds no reservation and there is nothing to
            // move. Tracked in the index only, which never touches the record: writing the payer here would
            // reopen that race, so the exposure ledger keeps missing it (EIP8141-GAP).
            resolvedPayer = payer;
            return true;
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

            // The head spec is only readable under the lock below, so the state is declared here for the
            // paymaster release in the finally and built once the spec is in hand.
            TxFilteringState state = default;

            _newHeadLock.EnterReadLock();
            try
            {
                IReleaseSpec headSpec = _specProvider.GetCurrentHeadSpec();
                // Observation and insertion share the head lock so an A -> B -> A transition cannot cross a validation publish unseen.
                ObserveHeadSpec(headSpec);
                state = new(tx, _accounts, headSpec);
                accepted = FilterTransactions(tx, handlingOptions, ref state);
                if (accepted)
                {
                    accepted = AddCore(tx, ref state, startBroadcast);
                }
                else
                {
                    if (accepted == AcceptTxResult.IncompleteBlobData && tx.Hash is not null)
                    {
                        _hashCache.DeleteFromCurrentBlock(tx.Hash);
                    }

                    Metrics.PendingTransactionsDiscarded++;
                }
            }
            finally
            {
                // The cap counts ahead of the filters that follow it, so anything leaving the transaction
                // unpooled — a later rejection or a throw — hands the slot back; AddCore clears the flag
                // once the pool owns it or has released it itself.
                if (state.PaymasterReserved && PendingPaymasterCache.KeyFor(tx) is Address paymaster)
                {
                    _pendingPaymasters.Decrement(paymaster);
                }

                _newHeadLock.ExitReadLock();
            }

            if (accepted != AcceptTxResult.Invalid
                && accepted != AcceptTxResult.InvalidBlobProofs)
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

        public AcceptTxResult ValidateTxForBlobSampling(Transaction tx)
        {
            if (!tx.SupportsBlobs)
            {
                return AcceptTxResult.Invalid;
            }

            _newHeadLock.EnterReadLock();
            try
            {
                TxFilteringState state = new(tx, _accounts, _specProvider.GetCurrentHeadSpec());
                return FilterTransactions(tx, TxHandlingOptions.None, ref state, skipSamplingDeferredFilters: true);
            }
            finally
            {
                _newHeadLock.ExitReadLock();
            }
        }

        private AcceptTxResult FilterTransactions(
            Transaction tx,
            TxHandlingOptions handlingOptions,
            ref TxFilteringState state,
            bool skipSamplingDeferredFilters = false)
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
                if (skipSamplingDeferredFilters
                    && filters[i] is AlreadyKnownTxFilter or BlobProofsTxFilter)
                {
                    continue;
                }

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

            // EIP-8141: a successful insert hands the payer exposure and paymaster slot to the pool,
            // released on Removed. Every other exit, a throw included, must release them here or they leak.
            bool reservationSettled = false;
            try
            {
                bool eip1559Enabled = headSpec.IsEip1559Enabled;
                UInt256 effectiveGasPrice = tx.CalculateEffectiveGasPrice(eip1559Enabled, _headInfo.CurrentBaseFee);
                TxDistinctSortedPool relevantPool = (tx.CarriesBlobs ? _blobTransactions : _transactions);

                relevantPool.TryGetBucketsWorstValue(tx.SenderAddress!, out Transaction? worstTx);
                tx.GasBottleneck = (worstTx is null || effectiveGasPrice <= worstTx.GasBottleneck)
                    ? effectiveGasPrice
                    : worstTx.GasBottleneck;

                bool inserted = relevantPool.TryInsert(tx.Hash!, tx, out Transaction? removed);
                // The reservation is now the pool's, or was already released by a self-eviction Removed.
                reservationSettled = true;
                state.PaymasterReserved = false;

                if (!inserted)
                {
                    // it means it failed on adding to the pool - it is possible when new tx has the same sender
                    // and nonce as already existent tx and is not good enough to replace it
                    // No Removed event fires for this tx, so release the reservations it took.
                    ReleaseFrameTxReservations(tx);
                    Metrics.PendingTransactionsPassedFiltersButCannotReplace++;
                    return AcceptTxResult.ReplacementNotAllowed;
                }

                if (tx.Hash == removed?.Hash)
                {
                    // it means it was added and immediately evicted - pool was full of better txs
                    // Its Removed already released the reservation, so a tx kept only by the broadcaster
                    // under-counts its payer and its paymaster; accepted, as the broadcaster has no hook to release on.
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
                    ReleaseFrameTxReservations(tx);
                    state.PaymasterReserved = false;
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
        /// (<see cref="FrameTxPayerExposureFilter"/>) and the slot its paymaster took
        /// (<see cref="FrameTxPaymasterFilter"/>), once the transaction leaves the pool.
        /// </summary>
        /// <remarks>
        /// Covers eviction, replacement, inclusion and reorg removal (all funnel through the pool
        /// <c>Removed</c> event) plus the paths in <see cref="AddCore"/> that never insert.
        /// </remarks>
        private void ReleaseFrameTxReservations(Transaction tx)
        {
            if (TryGetPayerReservation(tx, out Address? payer, out UInt256 maxCost))
            {
                _payerExposure.Subtract(payer, maxCost);
            }

            if (PendingPaymasterCache.KeyFor(tx) is Address paymaster)
            {
                _pendingPaymasters.Decrement(paymaster);
            }
        }

        /// <summary>The exposure <paramref name="tx"/> holds against its payer for as long as it stays pending.</summary>
        /// <remarks>
        /// Shared with the bookkeeping check so it cannot drift from what the pool actually releases. Replays
        /// what admission recorded rather than re-pricing: a pooled blob-carrying frame transaction is a light
        /// record with no frames to price, and the pricing spec moves with the head besides.
        /// </remarks>
        private static bool TryGetPayerReservation(Transaction tx, [NotNullWhen(true)] out Address? payer, out UInt256 maxCost)
        {
            payer = tx.SupportsFrames ? tx.PayerAddress : null;
            if (payer is null || tx.PayerExposure is not { } reserved)
            {
                maxCost = UInt256.Zero;
                return false;
            }

            maxCost = reserved;
            return true;
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
                if (KeyedNonceManager.UsesKeyedNonce(tx))
                {
                    if (!IsKeyedNonceCurrent(tx))
                    {
                        MarkForEviction(tx, allowLaterPoolReentrance: !IsKeyedNonceBehind(tx));
                    }
                    else
                    {
                        if (revalidation is not null)
                        {
                            ForkValidationResult keyedValidation = revalidation.Validate(tx);
                            if (!keyedValidation.Validation)
                            {
                                invalidatedByFork++;
                                // Keyed sequences advance independently, so no unconditional cascade here; a
                                // blob-carrying frame tx still cascades through MarkForEviction's CarriesBlobs arm.
                                MarkForEviction(tx, revalidation.RecordEviction(tx, keyedValidation));
                                continue;
                            }
                        }

                        if (tx.CheckForNotEnoughBalance(UInt256.Zero, balance, out _))
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
                evictNextTxs |= tx.CarriesBlobs || evictFollowingTransactions;
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

            if (markerMatches || isEmpty)
            {
                // Retained deletes are process-local and cannot exist during construction.
                _ = TryPublishValidatedSpec(headSpec, expectedMarker);
            }
            else
            {
                if (_specChangeValidationStorage is not null)
                {
                    Volatile.Write(ref _specChangeMarkerUnpublished, true);
                    _specChangeValidationStorage.SetSpecChangeValidationMarker(null);
                }
            }
        }

        private bool TryRevalidateCurrentSpec(long generation)
        {
            try
            {
                return TryRevalidateCurrentSpecCore(generation);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Warn($"TxPool failed to revalidate transactions after a protocol change with exception {exception}");
                return false;
            }
        }

        private bool TryRevalidateCurrentSpecCore(long generation)
        {
            IReleaseSpec spec;
            long headSpecGeneration;
            bool isEmpty;
            bool isValidated;
            string marker;

            _newHeadLock.EnterWriteLock();
            try
            {
                if (!IsRevalidationGenerationCurrent(generation))
                {
                    return false;
                }

                spec = _specProvider.GetCurrentHeadSpec();
                headSpecGeneration = ObserveHeadSpec(spec);
                marker = GetSpecChangeValidationMarker(spec);
                bool isSpecChangeMarkerUnpublished = Volatile.Read(ref _specChangeMarkerUnpublished);
                isValidated = ReferenceEquals(Volatile.Read(ref _validatedSpec), spec);
                if (isValidated && !isSpecChangeMarkerUnpublished)
                {
                    return true;
                }

                if (!isValidated)
                {
                    ReleaseForkInvalidatedHashesFor(spec);
                    InvalidateValidatedSpec();
                }

                isEmpty = _transactions.Count == 0 && _blobTransactions.Count == 0;
            }
            finally
            {
                _newHeadLock.ExitWriteLock();
            }

            ForkRevalidation? revalidation = isEmpty || isValidated
                ? null
                : new ForkRevalidation(this, spec, generation, headSpecGeneration);
            if (revalidation is { IsComplete: false })
            {
                return false;
            }

            _newHeadLock.EnterWriteLock();
            try
            {
                if (!CanApplyRevalidationResults(spec, headSpecGeneration))
                {
                    RecordAbandonedRevalidation(spec, generation);
                    return false;
                }

                if (revalidation is not null)
                {
                    UpdateGroupDelegate updateBucket = revalidation.UpdateBucket;
                    _transactions.UpdatePool(_accounts, updateBucket);
                    _blobTransactions.UpdatePoolForRevalidation(_accounts, updateBucket);

                    if (revalidation.RemovedCount != 0)
                    {
                        Metrics.PendingTransactionsEvicted += revalidation.RemovedCount;
                        if (_logger.IsInfo) _logger.Info($"Removed {revalidation.RemovedCount:N0} transactions invalid under {spec.Name} after the protocol change.");
                    }
                }

                if (!CanApplyRevalidationResults(spec, headSpecGeneration))
                {
                    InvalidateValidatedSpec();
                    RecordAbandonedRevalidation(spec, generation);
                    return true;
                }

                if (!TryPublishValidatedSpec(spec, marker))
                {
                    return false;
                }

                return true;
            }
            finally
            {
                _newHeadLock.ExitWriteLock();
            }
        }

        private bool IsRevalidationGenerationCurrent(long generation) =>
            !_cts.IsCancellationRequested && generation == Volatile.Read(ref _headGeneration);

        private bool CanApplyRevalidationResults(IReleaseSpec spec, long headSpecGeneration)
        {
            HeadSpecObservation? observation = Volatile.Read(ref _headSpecObservation);
            return !_cts.IsCancellationRequested
                && observation is not null
                && observation.Generation == headSpecGeneration
                && ReferenceEquals(observation.Spec, spec)
                && ReferenceEquals(spec, _specProvider.GetCurrentHeadSpec());
        }

        private bool CanContinueRevalidation(IReleaseSpec spec, long generation, long headSpecGeneration)
        {
            HeadSpecObservation? observation = Volatile.Read(ref _headSpecObservation);
            if (!_cts.IsCancellationRequested
                && observation is not null
                && observation.Generation == headSpecGeneration
                && ReferenceEquals(observation.Spec, spec))
            {
                return true;
            }

            RecordAbandonedRevalidation(spec, generation);
            return false;
        }

        private void RecordAbandonedRevalidation(IReleaseSpec spec, long generation)
        {
            bool isCancellationRequested = _cts.IsCancellationRequested;
            long currentGeneration = Volatile.Read(ref _headGeneration);
            string reason;
            if (isCancellationRequested)
            {
                reason = "shutdown was requested";
            }
            else
            {
                IReleaseSpec currentSpec = _specProvider.GetCurrentHeadSpec();
                reason = !ReferenceEquals(spec, currentSpec)
                    ? $"the head specification changed from {spec.Name} to {currentSpec.Name}"
                    : $"the head changed during the pass (generation {generation}, now {currentGeneration})";
            }

            if (_logger.IsDebug) _logger.Debug($"Abandoned transaction pool revalidation for {spec.Name} because {reason}.");

            if (!isCancellationRequested)
            {
                int consecutiveAbandonments = Interlocked.Increment(ref _consecutiveRevalidationAbandonments);
                if (consecutiveAbandonments == RevalidationAbandonmentWarningThreshold && _logger.IsWarn)
                {
                    _logger.Warn($"Transaction pool revalidation was abandoned for {RevalidationAbandonmentWarningThreshold} consecutive heads; block production will validate fork-sensitive transaction state until a pass completes.");
                }
            }
        }

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

        /// <summary>
        /// Attempts to publish the validated specification and its persistence marker.
        /// </summary>
        /// <returns><see langword="false"/> when a retained blob delete cannot yet be flushed.</returns>
        private bool TryPublishValidatedSpec(IReleaseSpec spec, string marker)
        {
            bool deletesFlushed = _blobTransactions.TryFlushPendingRevalidationDeletes();

            lock (_forkStateLock)
            {
                Interlocked.Increment(ref _forkStateVersion);
                try
                {
                    if (deletesFlushed)
                    {
                        _specChangeValidationStorage?.SetSpecChangeValidationMarker(marker);
                        Volatile.Write(ref _specChangeMarkerUnpublished, false);
                        _consecutiveMarkerPublicationDeferrals = 0;
                    }
                    else
                    {
                        Volatile.Write(ref _specChangeMarkerUnpublished, true);
                        _consecutiveMarkerPublicationDeferrals++;
                    }

                    Volatile.Write(ref _validatedSpec, spec);
                    Interlocked.Exchange(ref _consecutiveRevalidationAbandonments, 0);
                }
                finally
                {
                    Interlocked.Increment(ref _forkStateVersion);
                }
            }

            if (!deletesFlushed
                && _consecutiveMarkerPublicationDeferrals == MarkerPublicationDeferralWarningThreshold
                && _logger.IsWarn)
            {
                _logger.Warn("Transaction pool validation marker publication remains deferred while a blob persistence update completes.");
            }
            else if (!deletesFlushed && _logger.IsDebug)
            {
                _logger.Debug("Deferred transaction pool validation marker publication while a blob persistence update completes.");
            }

            return deletesFlushed;
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
                    if (_specChangeValidationStorage is not null)
                    {
                        Volatile.Write(ref _specChangeMarkerUnpublished, true);
                        _specChangeValidationStorage.SetSpecChangeValidationMarker(null);
                    }
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
            return FormattableString.Invariant($"1|{ProductInfo.Version}|{ProductInfo.Commit}|{_specChangeValidationFingerprint}|{spec.Name}")
                + FormattableString.Invariant($"|{spec.IsEip2Enabled}|{spec.IsEip155Enabled}|{spec.ValidateChainId}|{spec.IsEip2028Enabled}")
                + FormattableString.Invariant($"|{spec.IsEip2780Enabled}|{spec.IsEip2930Enabled}|{spec.MaxInitCodeSize}")
                + FormattableString.Invariant($"|{spec.IsEip1559Enabled}|{spec.IsEip3860Enabled}|{spec.IsEip4844Enabled}|{spec.IsEip7623Enabled}")
                + FormattableString.Invariant($"|{spec.IsEip7702Enabled}|{spec.IsEip7976Enabled}|{spec.IsEip7981Enabled}|{spec.IsEip8037Enabled}|{spec.IsEip8038Enabled}")
                + FormattableString.Invariant($"|{spec.IsEip8141Enabled}|{spec.IsEip8250Enabled}")
                + FormattableString.Invariant($"|{gasCosts.TxDataNonZeroMultiplier}|{gasCosts.TotalCostFloorPerToken}|{gasCosts.MaxBlobGasPerBlock}|{gasCosts.MaxBlobGasPerTx}")
                + FormattableString.Invariant($"|{spec.GetTxGasLimitCap()}|{spec.BlobProofVersion}");
        }

        /// <summary>
        /// One pass of pool revalidation against a newly activated release specification.
        /// </summary>
        /// <remarks>
        /// Validation is collected outside the head lock. Persistent blob bodies are read from storage in bounded
        /// batches, avoiding the blob-pool lock and sidecar-cache pollution on the normal path. Storage misses fall
        /// back to the persistent pool because it can own the only copy after a write failed or while it is deferred.
        /// Only the short removal pass runs under the head write lock.
        /// </remarks>
        private sealed class ForkRevalidation
        {
            private const int BlobReadBatchSize = 16;

            private readonly TxPool _pool;
            private readonly IReleaseSpec _spec;
            private readonly Dictionary<Hash256, ForkValidationResult> _invalidTransactions = [];

            public ForkRevalidation(TxPool pool, IReleaseSpec spec, long generation, long headSpecGeneration)
            {
                _pool = pool;
                _spec = spec;
                IsComplete = FindInvalidTransactions(generation, headSpecGeneration);
            }

            public bool IsComplete { get; }

            /// <summary>How many transactions the pass has dropped as invalid under the new specification.</summary>
            public int RemovedCount { get; private set; }

            public void UpdateBucket(in AccountStruct account, EnhancedSortedSet<Transaction> transactions, ref Transaction? lastElement, UpdateTransactionDelegate updateTx) =>
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

            private bool FindInvalidTransactions(long generation, long headSpecGeneration)
            {
                Transaction[] transactions = _pool._transactions.GetSnapshot();
                for (int i = 0; i < transactions.Length; i++)
                {
                    if (!_pool.CanContinueRevalidation(_spec, generation, headSpecGeneration))
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
                    if (!_pool.CanContinueRevalidation(_spec, generation, headSpecGeneration))
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

                    if (batchCount == BlobReadBatchSize)
                    {
                        if (!ProcessBlobBatch(batchCount, generation, headSpecGeneration, keys, hashes, fullTransactions))
                        {
                            return false;
                        }

                        batchCount = 0;
                    }
                }

                return batchCount == 0 || ProcessBlobBatch(batchCount, generation, headSpecGeneration, keys, hashes, fullTransactions);
            }

            private bool ProcessBlobBatch(
                int count,
                long generation,
                long headSpecGeneration,
                TxLookupKey[] keys,
                Hash256[] hashes,
                Transaction?[] fullTransactions)
            {
                Array.Clear(fullTransactions, 0, count);
                _pool._blobTxStorage.TryGetMany(keys, count, fullTransactions);

                for (int i = 0; i < count; i++)
                {
                    if (!_pool.CanContinueRevalidation(_spec, generation, headSpecGeneration))
                    {
                        return false;
                    }

                    Transaction? fullTransaction = fullTransactions[i];
                    // A failed or deferred storage write can leave the persistent pool owning the only full body.
                    if (fullTransaction is null
                        && !_pool._blobTransactions.TryGetValue(hashes[i], out fullTransaction))
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
                if (KeyedNonceManager.UsesKeyedNonce(txn) ? IsKeyedNonceCurrent(txn) : txn.Nonce == currentNonce)
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
                int invalidatedByFork = 0;
                foreach (Transaction transaction in transactions)
                {
                    bool allowLaterPoolReentrance = true;
                    if (revalidation is not null)
                    {
                        ForkValidationResult validation = revalidation.Validate(transaction);
                        if (!validation.Validation)
                        {
                            invalidatedByFork++;
                            allowLaterPoolReentrance = revalidation.RecordEviction(transaction, validation);
                        }
                    }

                    if (allowLaterPoolReentrance)
                    {
                        _hashCache.DeleteFromLongTerm(transaction.Hash!);
                    }

                    updateTx(transactions, transaction, changedGasBottleneck: null, lastElement);
                }

                return invalidatedByFork;
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

        /// <summary>Removes a frame transaction that block production dropped because its frames did not approve payment.</summary>
        /// <remarks>The long-term cache is cleared, unlike in <see cref="RemoveExpiredFrameTransactions"/>: a payment failure turns on chain state that can change.</remarks>
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

        public bool TryGetPendingTransactionWithoutBlobs(Hash256 hash, [NotNullWhen(true)] out Transaction? transaction)
        {
            if ((_transactions.TryGetValue(hash, out transaction)
                || _blobTransactions.TryGetValueWithoutBlobs(hash.ValueHash256, out transaction)
                || _broadcaster.TryGetPersistentTx(hash, out transaction))
                && transaction is not null)
            {
                transaction = BlobTransactionPayload.Elide(transaction);
                return true;
            }

            transaction = default;
            return false;
        }

        public bool TryGetPendingBlobTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction? blobTransaction) =>
            _blobTransactions.TryGetValue(hash, out blobTransaction);

        /// <inheritdoc/>
        public PendingTransactionsView GetPendingForProduction(BlockHeader targetBlock, bool filterToReadyTx, UInt256 baseFee)
        {
            long forkStateVersion = Volatile.Read(ref _forkStateVersion);
            IDictionary<AddressAsKey, Transaction[]> transactions = filterToReadyTx
                ? GetPendingTransactionsBySender(true, baseFee)
                : GetPendingTransactionsBySender();
            IDictionary<AddressAsKey, Transaction[]> blobTransactions = GetPendingLightBlobTransactionsBySender(filterToReadyTx, baseFee);

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

        public void ForgetRejectedBlobTransaction(Hash256 hash) => _hashCache.DeleteFromCurrentBlock(hash);

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
            _headInfo.HeadChanged -= OnHeadChange;
            _headBlocksChannel.Writer.Complete();
            _revalidationChannel.Writer.Complete();
            _transactions.Inserted -= OnInsertedTx;
            _transactions.Removed -= OnRemovedTx;
            _blobTransactions.Inserted -= OnInsertedTx;
            _blobTransactions.Removed -= OnRemovedTx;
            // Removed no longer fires, so anything still reserved would be counted by a gauge no pool can decrement.
            _payerExposure.Clear();

            await _retryCache.DisposeAsync();
            await _headProcessing;
            await _revalidationProcessing;
            _broadcaster.Dispose();
            (_blobTransactions as IDisposable)?.Dispose();
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

        private sealed record HeadSpecObservation(IReleaseSpec Spec, long Generation);

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

    /// <summary>
    /// Retains the newest requested revalidation generation independently of the lossy wake-up channel.
    /// </summary>
    /// <remarks>
    /// Producers must update the generation before signalling the channel. The consumer must drain pending
    /// signals before reading <see cref="Generation"/>, so every consumed signal is covered by the observed generation.
    /// </remarks>
    internal sealed class LatestRevalidationRequest
    {
        private long _generation;

        public long Generation => Volatile.Read(ref _generation);

        public void Update(long generation)
        {
            long current = Volatile.Read(ref _generation);
            while (generation > current)
            {
                long observed = Interlocked.CompareExchange(ref _generation, generation, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
