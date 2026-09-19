// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;
using static Nethermind.TxPool.Comparison.TxComparisonResult;

[assembly: InternalsVisibleTo("Nethermind.AuRa.Test")]

namespace Nethermind.Consensus.Producers
{
    public class TxPoolTxSource(
        ITxPool? transactionPool,
        ISpecProvider? specProvider,
        ITransactionComparerProvider? transactionComparerProvider,
        ILogManager? logManager,
        ITxFilterPipeline? txFilterPipeline,
        IBlocksConfig blocksConfig,
        [KeyFilter(ITxValidator.SpecChangeTxValidatorKey)] ITxValidator? specChangeTxValidator)
        : ITxSource
    {
        private const ulong BlobConsiderationMultiplier = 5;
        private const ulong RejectedBlobReadMultiplier = 10;

        private readonly ITxPool _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
        private readonly ITransactionComparerProvider _transactionComparerProvider = transactionComparerProvider ?? throw new ArgumentNullException(nameof(transactionComparerProvider));
        private readonly ITxFilterPipeline _txFilterPipeline = txFilterPipeline ?? throw new ArgumentNullException(nameof(txFilterPipeline));
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly ITxValidator _specChangeTxValidator = specChangeTxValidator ?? throw new ArgumentNullException(nameof(specChangeTxValidator));
        protected readonly ILogger _logger = logManager?.GetClassLogger<TxPoolTxSource>() ?? throw new ArgumentNullException(nameof(logManager));

        public IEnumerable<Transaction> GetTransactions(
            BlockHeader parent,
            BlockHeader targetBlock,
            ulong gasLimit,
            PayloadAttributes? payloadAttributes = null,
            bool filterSource = false)
        {
            IReleaseSpec spec = _specProvider.GetSpec(targetBlock);
            UInt256 baseFee = BaseFeeCalculator.Calculate(parent, spec);
            PendingTransactionsView pending = _transactionPool.GetPendingForProduction(targetBlock, filterSource, baseFee);
            bool isRevalidatedForTarget = pending.IsRevalidated;
            IReadOnlyDictionary<AddressAsKey, Transaction[]> pendingTransactions = pending.Transactions;
            IReadOnlyDictionary<AddressAsKey, Transaction[]> pendingBlobTransactionsEquivalences = pending.BlobTransactions;
            IComparer<Transaction> comparer = GetComparer(parent, new BlockPreparationContext(baseFee, targetBlock.Number))
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not lose transactions we need to differentiate on their identity which provided comparer might not be doing

            Func<Transaction, bool> filter = tx => _txFilterPipeline.Execute(tx, parent, spec);
            bool BlobFilter(Transaction tx) => HasFullBlobData(tx) && filter(tx);
            // A revalidated pool has already rejected everything whose validity changes with the target spec.
            Func<Transaction, bool> pendingTxFilter = isRevalidatedForTarget
                ? filter
                : tx => filter(tx) && IsForkSensitiveStateValid(tx, spec);

            ulong maxBlobCount = spec.MaxProductionBlobCount(blocksConfig.BlockProductionBlobLimit);
            IEnumerable<Transaction> transactions = GetOrderedTransactions(
                pendingTransactions,
                comparer,
                pendingTxFilter,
                gasLimit);
            if (_logger.IsTrace) _logger.Trace($"Collecting pending transactions at block gas limit {gasLimit}.");

            int checkedTransactions = 0;
            int selectedTransactions = 0;

            using ArrayPoolList<Transaction> selectedBlobTxs = new((int)maxBlobCount);

            Dictionary<Hash256, Transaction>? fullBlobTxs = null;
            if (pendingBlobTransactionsEquivalences.Count > 0)
            {
                IEnumerable<(Transaction tx, ulong blobChain)> blobTransactions = GetOrderedBlobTransactions(
                    pendingBlobTransactionsEquivalences,
                    comparer,
                    BlobFilter,
                    maxBlobCount);
                fullBlobTxs = SelectBlobTransactions(blobTransactions, parent, spec, baseFee, selectedBlobTxs, maxBlobCount, !isRevalidatedForTarget);
            }

            foreach (Transaction tx in transactions)
            {
                checkedTransactions++;

                if (tx.SenderAddress is null)
                {
                    _transactionPool.RemoveTransaction(tx.Hash!);
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (null sender) {tx.ToShortString()}");
                    continue;
                }

                while (selectedBlobTxs.Count > 0 && comparer.Compare(selectedBlobTxs[0], tx) < Equal)
                {
                    Transaction blobTx = selectedBlobTxs[0];
                    if (TryResolveSelectedBlob(blobTx, out Transaction? fullBlobTx))
                    {
                        yield return fullBlobTx;
                    }
                    selectedBlobTxs.RemoveAt(0);
                }

                if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be potentially included in block.");

                selectedTransactions++;
                yield return tx;
            }

            if (selectedBlobTxs.Count > 0)
            {
                foreach (Transaction blobTx in selectedBlobTxs)
                {
                    if (TryResolveSelectedBlob(blobTx, out Transaction? fullBlobTx))
                    {
                        yield return fullBlobTx;
                    }
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Potentially selected {selectedTransactions} out of {checkedTransactions} pending transactions checked.");

            bool TryResolveSelectedBlob(Transaction blobTx, [NotNullWhen(true)] out Transaction? fullBlobTx)
            {
                if (fullBlobTxs is not null
                    && blobTx.Hash is Hash256 hash
                    && fullBlobTxs.TryGetValue(hash, out fullBlobTx))
                {
                    return true;
                }

                return TryResolveBlob(blobTx, spec, out fullBlobTx);
            }
        }

        private static bool HasFullBlobData(Transaction tx) => tx switch
        {
            LightTransaction lightTx => lightTx.BlobCellMask.IsFull,
            { NetworkWrapper: ShardBlobNetworkWrapper wrapper } => wrapper.HasFullBlobs(),
            _ => false
        };

        private Dictionary<Hash256, Transaction>? SelectBlobTransactions(
            IEnumerable<(Transaction tx, ulong blobChain)> blobTransactions,
            BlockHeader parent,
            IReleaseSpec spec,
            in UInt256 baseFee,
            ArrayPoolList<Transaction> selectedBlobTxs,
            ulong maxBlobs,
            bool validateForkSensitiveState)
        {
            // Allow more rejected sidecar loads than valid candidates, but keep storage work bounded. Light-only
            // rejections do no I/O and are bounded by the pool size instead, so they do not consume this budget.
            ulong maxBlobsToConsider = maxBlobs * BlobConsiderationMultiplier;
            ulong maxRejectedBlobsToConsider = maxBlobs * RejectedBlobReadMultiplier;
            ulong countOfRemainingBlobs = 0UL;
            ulong consideredBlobCount = 0UL;
            ulong rejectedBlobCount = 0UL;
            Dictionary<Hash256, Transaction>? fullBlobTxs = null;
            ILightTxValidator? lightTxValidator = _specChangeTxValidator as ILightTxValidator;

            if (!TryUpdateFeePerBlobGas(parent, spec, out UInt256 feePerBlobGas))
            {
                if (_logger.IsTrace) _logger.Trace($"Declining blobs, failed to calculate gas price.");
                return null;
            }

            ArrayPoolList<(Transaction tx, ulong blobChain)>? candidates = null;
            try
            {
                foreach ((Transaction blobTx, ulong blobChain) in blobTransactions)
                {
                    ulong txBlobCount = (ulong)blobTx.GetBlobCount();
                    if (txBlobCount > maxBlobs)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, not enough blob space.");
                        continue;
                    }

                    if (feePerBlobGas > blobTx.MaxFeePerBlobGas)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, data gas fee is too low.");
                        continue;
                    }

                    if (validateForkSensitiveState)
                    {
                        if (blobTx is LightTransaction lightTransaction
                            && lightTxValidator is not null
                            && !lightTxValidator.IsWellFormedLight(lightTransaction, spec))
                        {
                            continue;
                        }

                        if (!TryResolveBlob(blobTx, spec, out Transaction? fullBlobTx)
                            || !IsForkSensitiveStateValid(fullBlobTx, spec))
                        {
                            rejectedBlobCount += txBlobCount;
                            if (rejectedBlobCount > maxRejectedBlobsToConsider)
                            {
                                break;
                            }

                            continue;
                        }

                        if (blobTx.Hash is Hash256 hash)
                        {
                            (fullBlobTxs ??= [])[hash] = fullBlobTx;
                        }
                    }

                    consideredBlobCount += txBlobCount;
                    bool reachedConsiderationLimit = consideredBlobCount > maxBlobsToConsider;

                    if (txBlobCount == 1UL && candidates is null)
                    {
                        selectedBlobTxs.Add(blobTx);
                        if ((ulong)selectedBlobTxs.Count == maxBlobs)
                        {
                            // Early exit, have complete set of 1 blob txs with maximal priority fees
                            // No need to consider other tx.
                            return GetSelectedFullBlobTransactions();
                        }
                    }
                    else
                    {
                        candidates ??= new(16);

                        candidates.Add((blobTx, blobChain));
                        countOfRemainingBlobs += txBlobCount;
                    }

                    if (reachedConsiderationLimit)
                    {
                        // Reached max blobs to consider, should have enough to fill the block.
                        break;
                    }
                }
            }
            catch
            {
                candidates?.Dispose();
                throw;
            }

            // No leftover candidates
            if (candidates is null) return GetSelectedFullBlobTransactions();

            using (candidates)
            {
                // We have leftover candidates. Check how many blob slots remain.
                ulong leftoverCapacity = maxBlobs - (ulong)selectedBlobTxs.Count;
                if (countOfRemainingBlobs <= leftoverCapacity)
                {
                    foreach ((Transaction tx, ulong blobChain) tx in candidates.AsSpan())
                    {
                        selectedBlobTxs.Add(tx.tx);
                    }
                }
                else
                {
                    ChooseBestBlobTransactions(candidates, (int)leftoverCapacity, baseFee, selectedBlobTxs);
                }
            }

            return GetSelectedFullBlobTransactions();

            Dictionary<Hash256, Transaction>? GetSelectedFullBlobTransactions()
            {
                if (fullBlobTxs is null || fullBlobTxs.Count == selectedBlobTxs.Count)
                {
                    return fullBlobTxs;
                }

                Dictionary<Hash256, Transaction> selectedFullBlobTxs = new(selectedBlobTxs.Count);
                foreach (Transaction selectedBlobTx in selectedBlobTxs)
                {
                    if (selectedBlobTx.Hash is Hash256 hash
                        && fullBlobTxs.TryGetValue(hash, out Transaction? fullBlobTx))
                    {
                        selectedFullBlobTxs[hash] = fullBlobTx;
                    }
                }

                return selectedFullBlobTxs;
            }
        }

        /// <summary>
        /// Selects a subset of candidate transactions
        /// that maximizes the total fee without exceeding the available blob capacity.
        /// Uses a 1D knapsack dynamic programming approach to find the optimal selection.
        /// The chosen transactions are appended to <paramref name="selectedBlobTxs"/>.
        /// </summary>
        /// <param name="candidateTxs">A list of candidate blob transactions.</param>
        /// <param name="leftoverCapacity">The maximum remaining blob capacity available.</param>
        /// <param name="baseFee"></param>
        /// <param name="selectedBlobTxs">
        /// A collection to which the chosen transactions will be added.
        /// Existing entries remain untouched; chosen ones are appended at the end.
        /// </param>
        private static void ChooseBestBlobTransactions(
            ArrayPoolList<(Transaction tx, ulong blobChain)> candidateTxs,
            int leftoverCapacity,
            in UInt256 baseFee,
            ArrayPoolList<Transaction> selectedBlobTxs)
        {
            int maxCapacity = leftoverCapacity + 1;
            // The maximum total fee achievable with capacity
            using ArrayPoolListRef<ulong> dpFeesPooled = new(maxCapacity, maxCapacity);
            Span<ulong> dpFees = dpFeesPooled.AsSpan();

            using ArrayPoolBitMap isChosen = new(candidateTxs.Count * maxCapacity);

            // Build up the DP table to find the maximum total fee for each capacity.
            // Outer loop: go through each transaction (1-based index).
            // Inner loop: iterate capacity in descending order to avoid overwriting data needed for the calculation.
            for (int i = 0; i < candidateTxs.Count; i++)
            {
                (Transaction tx, ulong blobChain) = candidateTxs[i];

                if (!tx.TryCalculatePremiumPerGas(baseFee, out UInt256 premiumPerGas))
                {
                    // Skip any tx where tx can't cover the premium per gas.
                    continue;
                }

                // How many blobs does this tx actually consume?
                int blobCount = tx.GetBlobCount();
                // If this tx has explicit dependencies (i.e. it requires k prior blobs
                // from the *same address* to be in the block before it), include them here.
                // We'll need a capacity of blobDependenciesCount slots *plus* its own blobCount.
                int blobCapacityNeeded = (int)blobChain + blobCount;
                // Compute the total fee this tx contributes (premium * gas used).
                // Use actual gas used (SpentGas) when available as the tx may be using over-estimated gaslimit
                ulong feeValue = (ulong)premiumPerGas * tx.SpentGas;

                int dependencyIndex = -1;
                // If dependencies, look back for the one direct predecessor tx.
                // if blobDependenciesCount > 0, then we require *the* previous
                // nonce from the same address to also be chosen in order to
                // include this tx's extra blob-dependency slots.
                if (blobCapacityNeeded > blobCount)
                {
                    // scan backward from i–1 until you hit a tx from the same address
                    // this ensures we only link to the immediate prior-nonce.
                    for (int j = i - 1; j >= 0; j--)
                    {
                        Transaction required = candidateTxs[j].tx;
                        if (required.SenderAddress == tx.SenderAddress)
                        {
                            if (required.Nonce + 1 == tx.Nonce)
                            {
                                // only a match if it's exactly nonce–1
                                dependencyIndex = j;
                            }
                            // Stop as soon as we found the prior same sender tx
                            break;
                        }
                    }

                    if (dependencyIndex < 0)
                    {
                        // if we didn't find an immediate matching address with the prior nonce,
                        // so we *cannot* include this tx
                        continue;
                    }
                }

                // Iterate backward from maxBlobCapacity down to blobCount (from high to low to avoid overwrite)
                // so we only compute for valid capacities that can fit this transaction.
                for (int capacity = leftoverCapacity; capacity >= blobCapacityNeeded; capacity--)
                {
                    int previousCapacity = capacity - blobCount;
                    // We subtract only tx's own blobCount from capacity,
                    // because the dpFees index represents total blobs used;
                    // dependencies are "paid for" by only allowing this path
                    // if dependencyIndex was chosen at the smaller capacity.
                    ulong candidateFee = dpFees[previousCapacity] + feeValue;
                    // If this improves the max fee at [capacity], record it
                    if (candidateFee >= dpFees[capacity])
                    {
                        dpFees[capacity] = candidateFee;

                        isChosen[i * maxCapacity + capacity] = dependencyIndex < 0 ||
                            // with a dependency: only mark this tx as chosen
                            // if *its* predecessor was also marked in the smaller capacity.
                            isChosen[dependencyIndex * maxCapacity + previousCapacity];
                    }
                }
            }

            int start = selectedBlobTxs.Count;
            // Backtrack through 'choices' to find which transactions were actually chosen.
            int remainingCapacity = leftoverCapacity;
            for (int i = candidateTxs.Count - 1; i >= 0; i--)
            {
                if (isChosen[i * maxCapacity + remainingCapacity])
                {
                    Transaction tx = candidateTxs[i].tx;
                    int blobCount = tx.GetBlobCount();
                    selectedBlobTxs.Add(tx);
                    remainingCapacity -= blobCount;
                }
            }

            // The newly added items were added in reverse
            // restore original picking order.
            selectedBlobTxs.AsSpan()[start..].Reverse();
        }

        private bool TryResolveBlob(Transaction blobTx, IReleaseSpec spec, [NotNullWhen(true)] out Transaction? fullBlobTx)
        {
            fullBlobTx = blobTx;
            if (blobTx.NetworkWrapper is null
                && (blobTx.Hash is null || !_transactionPool.TryGetPendingBlobTransaction(blobTx.Hash, out fullBlobTx)))
            {
                if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, failed to get full version of this blob tx from TxPool.");
                return false;
            }

            if (fullBlobTx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
            {
                if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, missing blob data.");
                return false;
            }

            if (wrapper.Version != spec.BlobProofVersion)
            {
                if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, {spec.BlobProofVersion} is wanted, but tx's proof version is {wrapper.Version}.");
                return false;
            }

            if (!wrapper.HasFullBlobs())
            {
                if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, blob data is only sampled locally.");
                return false;
            }

            if (wrapper.Blobs.Length != blobTx.BlobVersionedHashes?.Length)
            {
                if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, incorrect blob count.");
                return false;
            }

            return true;
        }

        private bool IsForkSensitiveStateValid(Transaction tx, IReleaseSpec spec) =>
            _specChangeTxValidator.IsWellFormed(tx, spec);

        private bool TryUpdateFeePerBlobGas(BlockHeader parent, IReleaseSpec spec, out UInt256 feePerBlobGas)
        {
            ulong? excessDataGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
            if (excessDataGas is null)
            {
                if (_logger.IsTrace) _logger.Trace($"Declining blobs, the specification is not configured to handle shard blob transactions.");
                feePerBlobGas = UInt256.Zero;
                return false;
            }

            if (!BlobGasCalculator.TryCalculateFeePerBlobGas(excessDataGas.Value, spec.BlobBaseFeeUpdateFraction, out feePerBlobGas))
            {
                if (_logger.IsTrace) _logger.Trace($"Declining blobs, failed to calculate data gas price.");
                feePerBlobGas = UInt256.Zero;
                return false;
            }

            return true;
        }

        protected virtual IEnumerable<Transaction> GetOrderedTransactions(IReadOnlyDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, ulong gasLimit) =>
            Order(pendingTransactions, comparer, filter, gasLimit);

        private static IEnumerable<(Transaction tx, ulong blobChain)> GetOrderedBlobTransactions(IReadOnlyDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, ulong maxBlobs = 0ul) =>
            OrderCore<(Transaction tx, ulong resource), BlobOrdering>(pendingTransactions, comparer, filter, maxBlobs);

        protected virtual IComparer<Transaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext)
            => _transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        internal static IEnumerable<Transaction> Order(IReadOnlyDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, ulong gasLimit) =>
            OrderCore<Transaction, TransactionOrdering>(pendingTransactions, comparer, filter, gasLimit);

        private interface IOrdering<TResult>
        {
            static abstract TResult Select(Transaction transaction, ulong resource);
            static abstract ulong GetResource(Transaction transaction);
            static abstract bool EnforceSequentialNonces { get; }
        }

        private readonly struct TransactionOrdering : IOrdering<Transaction>
        {
            public static Transaction Select(Transaction transaction, ulong resource) => transaction;
            public static ulong GetResource(Transaction transaction) => transaction.BlockGasUsed;
            public static bool EnforceSequentialNonces => false;
        }

        private readonly struct BlobOrdering : IOrdering<(Transaction, ulong)>
        {
            public static (Transaction, ulong) Select(Transaction transaction, ulong resource) => (transaction, resource);
            public static ulong GetResource(Transaction transaction) => (ulong)transaction.GetBlobCount();
            public static bool EnforceSequentialNonces => true;
        }

        private static IEnumerable<TResult> OrderCore<TResult, TOrdering>(
            IReadOnlyDictionary<AddressAsKey, Transaction[]> pendingTransactions,
            IComparer<Transaction> comparer,
            Func<Transaction, bool> filter,
            ulong resourceLimit)
            where TOrdering : struct, IOrdering<TResult>
        {
            using ArrayPoolList<(Transaction[] bucket, int index, int heapIndex, ulong resource)> entries = new(pendingTransactions.Count);
            foreach (Transaction[] bucket in pendingTransactions.Values)
            {
                if (bucket.Length > 0) entries.Add((bucket, 0, entries.Count, 0));
            }

            // Heap slots store indices into stationary sender entries; sifting moves no managed references.
            int count = entries.Count;
            for (int i = count / 2 - 1; i >= 0; i--) SiftDown(entries.AsSpan(), count, entries[i].heapIndex, i, comparer);
            while (count > 0)
            {
                int entryIndex = entries[0].heapIndex;
                (Transaction[] bucket, int index, _, ulong resource) = entries[entryIndex];
                Transaction candidateTx = bucket[index];
                ulong totalResource = resource + TOrdering.GetResource(candidateTx);
                bool accepted = totalResource <= resourceLimit && filter(candidateTx);
                int nextIndex = index + 1;
                if (accepted && nextIndex < bucket.Length
                    && (!TOrdering.EnforceSequentialNonces
                        || candidateTx.Nonce != ulong.MaxValue
                        && bucket[nextIndex].Nonce == candidateTx.Nonce + 1))
                {
                    entries.AsSpan()[entryIndex].index = nextIndex;
                    entries.AsSpan()[entryIndex].resource = totalResource;
                    SiftDown(entries.AsSpan(), count, entryIndex, 0, comparer);
                }
                else
                {
                    count--;
                    if (count > 0) SiftAfterRemoval(entries.AsSpan(), count, entries[count].heapIndex, comparer);
                    entries.AsSpan()[entryIndex].bucket = null!;
                }

                if (accepted) yield return TOrdering.Select(candidateTx, resource);
            }
        }

        private static void SiftDown(
            Span<(Transaction[] bucket, int index, int heapIndex, ulong resource)> entries,
            int count,
            int item,
            int index,
            IComparer<Transaction> comparer)
        {
            Transaction tx = entries[item].bucket[entries[item].index];
            while (index < count / 2)
            {
                int child = index * 2 + 1;
                if (child + 1 < count && comparer.Compare(GetHeapTransaction(entries, child + 1), GetHeapTransaction(entries, child)) < 0) child++;
                if (comparer.Compare(tx, GetHeapTransaction(entries, child)) <= 0) break;
                entries[index].heapIndex = entries[child].heapIndex;
                index = child;
            }
            entries[index].heapIndex = item;
        }

        private static void SiftAfterRemoval(
            Span<(Transaction[] bucket, int index, int heapIndex, ulong resource)> entries,
            int count,
            int item,
            IComparer<Transaction> comparer)
        {
            Transaction tx = entries[item].bucket[entries[item].index];
            int index = 0;
            // The last heap item usually belongs near the bottom; compare it only on the ascent.
            while (index < count / 2)
            {
                int child = index * 2 + 1;
                if (child + 1 < count && comparer.Compare(GetHeapTransaction(entries, child + 1), GetHeapTransaction(entries, child)) < 0) child++;
                entries[index].heapIndex = entries[child].heapIndex;
                index = child;
            }

            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (comparer.Compare(tx, GetHeapTransaction(entries, parent)) > 0) break;
                entries[index].heapIndex = entries[parent].heapIndex;
                index = parent;
            }
            entries[index].heapIndex = item;
        }

        private static Transaction GetHeapTransaction(Span<(Transaction[] bucket, int index, int heapIndex, ulong resource)> entries, int index)
        {
            ref (Transaction[] bucket, int index, int heapIndex, ulong resource) entry = ref entries[entries[index].heapIndex];
            return entry.bucket[entry.index];
        }

        public bool SupportsBlobs => _transactionPool.SupportsBlobs;

        public override string ToString() => $"{nameof(TxPoolTxSource)}";

        private readonly ref struct ArrayPoolBitMap : IDisposable
        {
            private const int BitShiftPerInt64 = 6;
            private static int GetLengthOfBitLength(int n) => (n - 1 + (1 << BitShiftPerInt64)) >>> BitShiftPerInt64;

            private readonly ulong[] _array;

            public ArrayPoolBitMap(int size)
            {
                _array = ArrayPool<ulong>.Shared.Rent(GetLengthOfBitLength(size));
                _array.AsSpan().Clear();
            }

            public bool this[int i]
            {
                get => (_array[i >> BitShiftPerInt64] & (1UL << i)) != 0;
                set
                {
                    ref ulong element = ref _array[(uint)i >> BitShiftPerInt64];
                    ulong selector = (1UL << i);
                    if (value)
                    {
                        element |= selector;
                    }
                    else
                    {
                        element &= ~selector;
                    }
                }
            }

            public void Dispose() => ArrayPool<ulong>.Shared.Return(_array);
        }
    }
}
