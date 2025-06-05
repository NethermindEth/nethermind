// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

[assembly: InternalsVisibleTo("Nethermind.AuRa.Test")]

namespace Nethermind.Consensus.Producers
{
    public class TxPoolTxSource(
        ITxPool? transactionPool,
        ISpecProvider? specProvider,
        ITransactionComparerProvider? transactionComparerProvider,
        ILogManager? logManager,
        ITxFilterPipeline? txFilterPipeline)
        : ITxSource
    {
        private readonly ITxPool _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
        private readonly ITransactionComparerProvider _transactionComparerProvider = transactionComparerProvider ?? throw new ArgumentNullException(nameof(transactionComparerProvider));
        private readonly ITxFilterPipeline _txFilterPipeline = txFilterPipeline ?? throw new ArgumentNullException(nameof(txFilterPipeline));
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        protected readonly ILogger _logger = logManager?.GetClassLogger<TxPoolTxSource>() ?? throw new ArgumentNullException(nameof(logManager));

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        {
            long blockNumber = parent.Number + 1;
            IReleaseSpec spec = payloadAttributes is not null ? _specProvider.GetSpec(blockNumber, payloadAttributes.Timestamp) : _specProvider.GetSpec(parent);
            UInt256 baseFee = BaseFeeCalculator.Calculate(parent, spec);
            IDictionary<AddressAsKey, Transaction[]> pendingTransactions = filterSource ?
                _transactionPool.GetPendingTransactionsBySender(filterToReadyTx: true, baseFee) :
                _transactionPool.GetPendingTransactionsBySender();
            IDictionary<AddressAsKey, Transaction[]> pendingBlobTransactionsEquivalences = _transactionPool.GetPendingLightBlobTransactionsBySender();
            IComparer<Transaction> comparer = GetComparer(parent, new BlockPreparationContext(baseFee, blockNumber))
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not lose transactions we need to differentiate on their identity which provided comparer might not be doing

            Func<Transaction, bool> filter = (tx) => _txFilterPipeline.Execute(tx, parent);

            IEnumerable<Transaction> transactions = GetOrderedTransactions(pendingTransactions, comparer, filter, gasLimit);
            IEnumerable<(Transaction tx, long blobChain)> blobTransactions = GetOrderedBlobTransactions(pendingBlobTransactionsEquivalences, comparer, filter, (int)spec.MaxBlobCount);
            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at block gas limit {gasLimit}.");

            int checkedTransactions = 0;
            int selectedTransactions = 0;
            using ArrayPoolList<Transaction> selectedBlobTxs = new((int)spec.MaxBlobCount);

            SelectBlobTransactions(blobTransactions, parent, spec, baseFee, selectedBlobTxs);

            foreach (Transaction tx in transactions)
            {
                checkedTransactions++;

                if (tx.SenderAddress is null)
                {
                    _transactionPool.RemoveTransaction(tx.Hash!);
                    if (_logger.IsDebug) _logger.Debug($"Rejecting (null sender) {tx.ToShortString()}");
                    continue;
                }

                foreach (Transaction blobTx in PickBlobTxsBetterThanCurrentTx(selectedBlobTxs, tx, comparer))
                {
                    if (ResolveBlob(blobTx, out Transaction fullBlobTx))
                    {
                        yield return fullBlobTx;
                    }
                }

                if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be potentially included in block.");

                selectedTransactions++;
                yield return tx;
            }

            if (selectedBlobTxs.Count > 0)
            {
                foreach (Transaction blobTx in selectedBlobTxs)
                {
                    if (ResolveBlob(blobTx, out Transaction fullBlobTx))
                    {
                        yield return fullBlobTx;
                    }
                }
            }

            if (_logger.IsDebug) _logger.Debug($"Potentially selected {selectedTransactions} out of {checkedTransactions} pending transactions checked.");

            bool ResolveBlob(Transaction blobTx, out Transaction fullBlobTx)
            {
                if (TryGetFullBlobTx(blobTx, out fullBlobTx))
                {
                    ProofVersion? proofVersion = (fullBlobTx.NetworkWrapper as ShardBlobNetworkWrapper)?.Version;
                    if (spec.BlobProofVersion != proofVersion)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, {spec.BlobProofVersion} is wanted, but tx's proof version is {proofVersion}.");
                        return false;
                    }

                    return true;
                }

                if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, failed to get full version of this blob tx from TxPool.");
                return false;
            }
        }

        private static IEnumerable<Transaction> PickBlobTxsBetterThanCurrentTx(ArrayPoolList<Transaction> selectedBlobTxs, Transaction tx, IComparer<Transaction> comparer)
        {
            while (selectedBlobTxs.Count > 0)
            {
                Transaction blobTx = selectedBlobTxs[0];
                if (comparer.Compare(blobTx, tx) > 0)
                {
                    yield return blobTx;
                    selectedBlobTxs.Remove(blobTx);
                }
                else
                {
                    break;
                }
            }
        }

        private void SelectBlobTransactions(IEnumerable<(Transaction tx, long blobChain)> blobTransactions, BlockHeader parent, IReleaseSpec spec, in UInt256 baseFee, ArrayPoolList<Transaction> selectedBlobTxs)
        {
            int maxBlobsPerBlock = (int)spec.MaxBlobCount;
            int maxBlobsToConsider = maxBlobsPerBlock * 5;
            int countOfRemainingBlobs = 0;

            if (!TryUpdateFeePerBlobGas(parent, spec, out UInt256 feePerBlobGas))
            {
                if (_logger.IsTrace) _logger.Trace($"Declining blobs, failed to calculate gas price.");
                return;
            }

            ArrayPoolList<(Transaction tx, long blobChain)>? candidates = null;
            foreach ((Transaction blobTx, long blobChain) in blobTransactions)
            {
                int txBlobCount = blobTx.GetBlobCount();
                if (txBlobCount > maxBlobsPerBlock)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, not enough blob space.");
                    continue;
                }

                if (feePerBlobGas > blobTx.MaxFeePerBlobGas)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, data gas fee is too low.");
                    continue;
                }

                if (txBlobCount == 1 && candidates is null)
                {
                    selectedBlobTxs.Add(blobTx);
                    if (selectedBlobTxs.Count == maxBlobsPerBlock)
                    {
                        // Early exit, have complete set of 1 blob txs with maximal priority fees
                        // No need to consider other tx.
                        return;
                    }
                }
                else
                {
                    candidates ??= new(16);

                    candidates.Add((blobTx, blobChain));
                    countOfRemainingBlobs += txBlobCount;
                }

                if (countOfRemainingBlobs > maxBlobsToConsider)
                {
                    // Reached max blobs to consider, should have enough to fill the block.
                    break;
                }
            }

            // No leftover candidates
            if (candidates is null) return;

            using (candidates)
            {
                // We have leftover candidates. Check how many blob slots remain.
                int leftoverCapacity = maxBlobsPerBlock - selectedBlobTxs.Count;
                if (countOfRemainingBlobs <= leftoverCapacity)
                {
                    // We can take all, no optimal picking needed.
                    foreach (var tx in candidates.AsSpan())
                    {
                        selectedBlobTxs.Add(tx.tx);
                    }
                }
                else
                {
                    // Are more blobs than spaces, select optimal set to include
                    ChooseBestBlobTransactions(candidates, leftoverCapacity, baseFee, selectedBlobTxs);
                }
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
            ArrayPoolList<(Transaction tx, long blobChain)> candidateTxs,
            int leftoverCapacity,
            in UInt256 baseFee,
            ArrayPoolList<Transaction> selectedBlobTxs)
        {
            int maxCapacity = leftoverCapacity + 1;
            // The maximum total fee achievable with capacity
            using ArrayPoolList<ulong> dpFeesPooled = new(capacity: maxCapacity, count: maxCapacity);
            Span<ulong> dpFees = dpFeesPooled.AsSpan();

            using ArrayPoolBitMap isChosen = new(candidateTxs.Count * maxCapacity);

            // Build up the DP table to find the maximum total fee for each capacity.
            // Outer loop: go through each transaction (1-based index).
            // Inner loop: iterate capacity in descending order to avoid overwriting data needed for the calculation.
            for (int i = 0; i < candidateTxs.Count; i++)
            {
                (Transaction tx, long blobChain) = candidateTxs[i];

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
                long blobCapacityNeeded = blobChain + blobCount;
                // Compute the total fee this tx contributes (premium * gas used).
                // Use actual gas used (SpentGas) when available as the tx may be using over-estimated gaslimit
                ulong feeValue = (ulong)premiumPerGas * (ulong)tx.SpentGas;

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
                    // We subtract only tx's own blobCount from capacity,
                    // because the dpFees index represents total blobs used;
                    // dependencies are "paid for" by only allowing this path
                    // if dependencyIndex was chosen at the smaller capacity.
                    ulong candidateFee = dpFees[capacity - blobCount] + feeValue;
                    // If this improves the max fee at [capacity], record it
                    if (candidateFee >= dpFees[capacity])
                    {
                        dpFees[capacity] = candidateFee;

                        isChosen[i * maxCapacity + capacity] = dependencyIndex < 0 ||
                            // with a dependency: only mark this tx as chosen
                            // if *its* predecessor was also marked in the smaller capacity.
                            isChosen[dependencyIndex * maxCapacity + (capacity - blobCount)];
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

        private bool TryGetFullBlobTx(Transaction blobTx, [NotNullWhen(true)] out Transaction? fullBlobTx)
        {
            if (blobTx.NetworkWrapper is not null)
            {
                fullBlobTx = blobTx;
                return true;
            }

            fullBlobTx = null;
            return blobTx.Hash is not null && _transactionPool.TryGetPendingBlobTransaction(blobTx.Hash, out fullBlobTx);
        }

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

        protected virtual IEnumerable<Transaction> GetOrderedTransactions(IDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, long gasLimit) =>
            Order(pendingTransactions, comparer, filter, gasLimit);

        private static IEnumerable<(Transaction tx, long blobChain)> GetOrderedBlobTransactions(IDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, int maxBlobs = 0) =>
            OrderCore(pendingTransactions, comparer, static tx => tx.GetBlobCount(), filter, maxBlobs);

        protected virtual IComparer<Transaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext)
            => _transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        internal static IEnumerable<Transaction> Order(IDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, long gasLimit) =>
            OrderCore(pendingTransactions, comparer, static tx => tx.SpentGas, filter, gasLimit).Select(static tx => tx.tx);

        private static IEnumerable<(Transaction tx, long resource)> OrderCore(
            IDictionary<AddressAsKey, Transaction[]> pendingTransactions,
            IComparer<Transaction> comparer,
            Func<Transaction, long> resourceSelector,
            Func<Transaction, bool> filter,
            long resourceLimit)
        {
            using ArrayPoolList<IEnumerator<Transaction>> bySenderEnumerators = pendingTransactions
                .Select<KeyValuePair<AddressAsKey, Transaction[]>, IEnumerable<Transaction>>(static g => g.Value)
                .Select(static g => g.GetEnumerator())
                .ToPooledList(pendingTransactions.Count);

            try
            {
                DictionarySortedSet<Transaction, (IEnumerator<Transaction>, long)> transactions = SortEnumerators(bySenderEnumerators, comparer);

                while (transactions.Count > 0)
                {
                    (Transaction candidateTx, (IEnumerator<Transaction> enumerator, long resourceChain)) = transactions.Min;

                    transactions.Remove(candidateTx);

                    long totalResource = resourceChain + resourceSelector(candidateTx);
                    if (totalResource > resourceLimit)
                        continue;

                    if (!filter(candidateTx))
                        continue;

                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, (enumerator, totalResource));
                    }

                    yield return (candidateTx, resourceChain);
                }
            }
            finally
            {
                foreach (IEnumerator<Transaction> t in bySenderEnumerators.AsSpan())
                {
                    t.Dispose();
                }
            }
        }

        private static DictionarySortedSet<Transaction, (IEnumerator<Transaction>, long)> SortEnumerators(ArrayPoolList<IEnumerator<Transaction>> bySenderEnumerators, IComparer<Transaction> comparerWithIdentity)
        {
            DictionarySortedSet<Transaction, (IEnumerator<Transaction>, long)> transactions = new(comparerWithIdentity);

            foreach (IEnumerator<Transaction> enumerator in bySenderEnumerators.AsSpan())
            {
                if (enumerator.MoveNext())
                {
                    Transaction current = enumerator.Current!;
                    transactions.Add(current, (enumerator, 0));
                }
            }

            return transactions;
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
