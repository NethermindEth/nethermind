// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Config;
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
        IBlocksConfig blocksConfig)
        : ITxSource
    {
        private readonly ITxPool _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
        private readonly ITransactionComparerProvider _transactionComparerProvider = transactionComparerProvider ?? throw new ArgumentNullException(nameof(transactionComparerProvider));
        private readonly ITxFilterPipeline _txFilterPipeline = txFilterPipeline ?? throw new ArgumentNullException(nameof(txFilterPipeline));
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        protected readonly ILogger _logger = logManager?.GetClassLogger<TxPoolTxSource>() ?? throw new ArgumentNullException(nameof(logManager));

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        {
            ulong blockNumber = parent.Number + 1;
            IReleaseSpec spec = NextBlockSpecHelper.GetSpec(_specProvider, parent, payloadAttributes, blocksConfig);
            UInt256 baseFee = BaseFeeCalculator.Calculate(parent, spec);
            IDictionary<AddressAsKey, Transaction[]> pendingTransactions = filterSource ?
                _transactionPool.GetPendingTransactionsBySender(filterToReadyTx: true, baseFee) :
                _transactionPool.GetPendingTransactionsBySender();
            IDictionary<AddressAsKey, Transaction[]> pendingBlobTransactionsEquivalences = _transactionPool.GetPendingLightBlobTransactionsBySender();
            IComparer<Transaction> comparer = GetComparer(parent, new BlockPreparationContext(baseFee, blockNumber))
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not lose transactions we need to differentiate on their identity which provided comparer might not be doing

            Func<Transaction, bool> filter = tx => _txFilterPipeline.Execute(tx, parent, spec);

            ulong maxBlobCount = spec.MaxProductionBlobCount(blocksConfig.BlockProductionBlobLimit);
            IEnumerable<Transaction> transactions = GetOrderedTransactions(pendingTransactions, comparer, filter, gasLimit);
            IEnumerable<(Transaction tx, ulong blobChain)> blobTransactions = GetOrderedBlobTransactions(pendingBlobTransactionsEquivalences, comparer, filter, maxBlobCount);
            if (_logger.IsTrace) _logger.Trace($"Collecting pending transactions at block gas limit {gasLimit}.");

            int checkedTransactions = 0;
            int selectedTransactions = 0;

            using ArrayPoolList<Transaction> selectedBlobTxs = new((int)maxBlobCount);

            SelectBlobTransactions(blobTransactions, parent, spec, baseFee, selectedBlobTxs, maxBlobCount);

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

            if (_logger.IsTrace) _logger.Trace($"Potentially selected {selectedTransactions} out of {checkedTransactions} pending transactions checked.");

            bool ResolveBlob(Transaction blobTx, out Transaction fullBlobTx)
            {
                if (!TryGetFullBlobTx(blobTx, out fullBlobTx))
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, failed to get full version of this blob tx from TxPool.");
                    return false;
                }

                if (fullBlobTx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, missing blob data.");
                    return false;
                }

                if (spec.BlobProofVersion != wrapper.Version)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, {spec.BlobProofVersion} is wanted, but tx's proof version is {wrapper.Version}.");
                    return false;
                }

                if (wrapper.Blobs.Length != blobTx.BlobVersionedHashes.Length)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, incorrect blob count.");
                    return false;
                }

                return true;
            }
        }

        private static IEnumerable<Transaction> PickBlobTxsBetterThanCurrentTx(ArrayPoolList<Transaction> selectedBlobTxs, Transaction tx, IComparer<Transaction> comparer)
        {
            while (selectedBlobTxs.Count > 0)
            {
                Transaction blobTx = selectedBlobTxs[0];
                if (comparer.Compare(blobTx, tx) < Equal)
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

        private void SelectBlobTransactions(IEnumerable<(Transaction tx, ulong blobChain)> blobTransactions, BlockHeader parent, IReleaseSpec spec, in UInt256 baseFee, ArrayPoolList<Transaction> selectedBlobTxs, ulong maxBlobs)
        {
            ulong maxBlobsToConsider = maxBlobs * 5ul;
            ulong countOfRemainingBlobs = 0UL;

            if (!TryUpdateFeePerBlobGas(parent, spec, out UInt256 feePerBlobGas))
            {
                if (_logger.IsTrace) _logger.Trace($"Declining blobs, failed to calculate gas price.");
                return;
            }

            using ArrayPoolList<(Transaction tx, ulong blobChain)> candidates = new(16);
            foreach ((Transaction blobTx, ulong blobChain) in blobTransactions)
            {
                if (blobTx.SenderAddress is null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, sender is not resolved.");
                    continue;
                }

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

                candidates.Add((blobTx, blobChain));
                countOfRemainingBlobs += txBlobCount;

                if (countOfRemainingBlobs > maxBlobsToConsider)
                {
                    // Reached max blobs to consider, should have enough to fill the block.
                    break;
                }
            }

            if (candidates.Count == 0) return;

            ChooseBestBlobTransactions(candidates, (int)maxBlobs, baseFee, selectedBlobTxs);
        }

        /// <summary>
        /// Selects nonce-contiguous sender prefixes without exceeding the available blob capacity.
        /// When selectable transactions have different execution fees, the selection maximizes total
        /// execution fees and uses producer priority as a tie-break. When all selectable transaction
        /// execution fees are equal, producer priority is the primary objective. This intentionally
        /// preserves transaction ordering even when a bundle of lower-priority transactions would have
        /// higher aggregate execution fees.
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
            int capacityCount = leftoverCapacity + 1;
            using ArrayPoolListRef<UInt256> dpFeesPooled = new(capacityCount, capacityCount);
            using ArrayPoolListRef<UInt256> nextFeesPooled = new(capacityCount, capacityCount);
            using ArrayPoolListRef<int> dpSelectionStatesPooled = new(capacityCount, capacityCount);
            using ArrayPoolListRef<int> nextSelectionStatesPooled = new(capacityCount, capacityCount);
            using ArrayPoolListRef<bool> dpReachablePooled = new(capacityCount, capacityCount);
            using ArrayPoolListRef<bool> nextReachablePooled = new(capacityCount, capacityCount);
            Span<UInt256> dpFees = dpFeesPooled.AsSpan();
            Span<UInt256> nextFees = nextFeesPooled.AsSpan();
            Span<int> dpSelectionStates = dpSelectionStatesPooled.AsSpan();
            Span<int> nextSelectionStates = nextSelectionStatesPooled.AsSpan();
            Span<bool> dpReachable = dpReachablePooled.AsSpan();
            Span<bool> nextReachable = nextReachablePooled.AsSpan();
            dpSelectionStates.Fill(-1);
            dpReachable[0] = true;

            // A sender contributes either no transactions or one contiguous nonce prefix.
            // Treating each transaction as an independent knapsack item can discard a lower-value
            // predecessor that is required by a later, more valuable transaction.
            using ArrayPoolList<AddressAsKey> senders = new(candidateTxs.Count);
            using ArrayPoolListRef<int> candidateGroupIndexesPooled = new(candidateTxs.Count, candidateTxs.Count);
            Span<int> candidateGroupIndexes = candidateGroupIndexesPooled.AsSpan();
            candidateGroupIndexes.Fill(-1);
            for (int candidateIndex = 0; candidateIndex < candidateTxs.Count; candidateIndex++)
            {
                AddressAsKey sender = candidateTxs[candidateIndex].tx.SenderAddress!;
                int groupIndex = -1;
                for (int index = 0; index < senders.Count; index++)
                {
                    if (senders[index].Equals(sender))
                    {
                        groupIndex = index;
                        break;
                    }
                }

                if (groupIndex < 0)
                {
                    groupIndex = senders.Count;
                    senders.Add(sender);
                }

                candidateGroupIndexes[candidateIndex] = groupIndex;
            }

            // Precompute every eligible prefix once. Prefixes that contain a gap, have an invalid
            // premium, or already exceed capacity cannot participate in the DP or switch its objective.
            using ArrayPoolListRef<int> prefixBlobCountsPooled = new(candidateTxs.Count, candidateTxs.Count);
            using ArrayPoolListRef<UInt256> prefixFeesPooled = new(candidateTxs.Count, candidateTxs.Count);
            Span<int> prefixBlobCounts = prefixBlobCountsPooled.AsSpan();
            Span<UInt256> prefixFees = prefixFeesPooled.AsSpan();
            prefixBlobCounts.Fill(-1);
            UInt256? commonExecutionFee = null;
            bool allExecutionFeesEqual = true;
            for (int groupIndex = 0; groupIndex < senders.Count; groupIndex++)
            {
                ulong prefixBlobCount = 0;
                ulong? previousNonce = null;
                UInt256 prefixFee = UInt256.Zero;
                for (int candidateIndex = 0; candidateIndex < candidateTxs.Count; candidateIndex++)
                {
                    if (candidateGroupIndexes[candidateIndex] != groupIndex) continue;

                    (Transaction tx, ulong blobChain) = candidateTxs[candidateIndex];
                    if (blobChain != prefixBlobCount ||
                        previousNonce is not null &&
                        (previousNonce == ulong.MaxValue || tx.Nonce != previousNonce + 1) ||
                        !tx.TryCalculatePremiumPerGas(baseFee, out UInt256 premiumPerGas))
                    {
                        break;
                    }

                    prefixBlobCount += (ulong)tx.GetBlobCount();
                    if (prefixBlobCount > (ulong)leftoverCapacity) break;
                    previousNonce = tx.Nonce;

                    UInt256 executionFee = premiumPerGas * tx.SpentGas;
                    prefixFee += executionFee;
                    prefixBlobCounts[candidateIndex] = (int)prefixBlobCount;
                    prefixFees[candidateIndex] = prefixFee;
                    if (commonExecutionFee is null)
                    {
                        commonExecutionFee = executionFee;
                    }
                    else if (commonExecutionFee != executionFee)
                    {
                        allExecutionFeesEqual = false;
                    }
                }
            }

            using ArrayPoolListRef<(int CandidateIndex, int PreviousState)> selectionStates =
                new(candidateTxs.Count * capacityCount);
            using ArrayPoolListRef<bool> candidateMembershipPooled = new(candidateTxs.Count, candidateTxs.Count);
            using ArrayPoolListRef<bool> currentMembershipPooled = new(candidateTxs.Count, candidateTxs.Count);
            Span<bool> candidateMembership = candidateMembershipPooled.AsSpan();
            Span<bool> currentMembership = currentMembershipPooled.AsSpan();

            for (int groupIndex = 0; groupIndex < senders.Count; groupIndex++)
            {
                dpFees.CopyTo(nextFees);
                dpSelectionStates.CopyTo(nextSelectionStates);
                dpReachable.CopyTo(nextReachable);

                for (int previousCapacity = 0; previousCapacity <= leftoverCapacity; previousCapacity++)
                {
                    if (!dpReachable[previousCapacity]) continue;

                    int prefixState = dpSelectionStates[previousCapacity];
                    for (int candidateIndex = 0; candidateIndex < candidateTxs.Count; candidateIndex++)
                    {
                        if (candidateGroupIndexes[candidateIndex] != groupIndex) continue;

                        int prefixBlobCount = prefixBlobCounts[candidateIndex];
                        if (prefixBlobCount < 0) break;
                        int capacity = previousCapacity + prefixBlobCount;
                        if (capacity > leftoverCapacity) break;

                        selectionStates.Add((candidateIndex, prefixState));
                        prefixState = selectionStates.Count - 1;

                        UInt256 candidateFee = dpFees[previousCapacity] + prefixFees[candidateIndex];
                        bool hasHigherPriority = !nextReachable[capacity] || IsHigherPrioritySelection(
                            selectionStates.AsSpan(), prefixState, nextSelectionStates[capacity],
                            candidateMembership, currentMembership);
                        bool improvesSelection = !nextReachable[capacity] || (allExecutionFeesEqual
                            ? hasHigherPriority
                            : candidateFee > nextFees[capacity] ||
                              candidateFee == nextFees[capacity] && hasHigherPriority);
                        if (improvesSelection)
                        {
                            nextReachable[capacity] = true;
                            nextFees[capacity] = candidateFee;
                            nextSelectionStates[capacity] = prefixState;
                        }
                    }
                }

                nextFees.CopyTo(dpFees);
                nextSelectionStates.CopyTo(dpSelectionStates);
                nextReachable.CopyTo(dpReachable);
            }

            int bestState = -1;
            UInt256 bestFee = UInt256.Zero;
            for (int capacity = 1; capacity <= leftoverCapacity; capacity++)
            {
                if (!dpReachable[capacity]) continue;

                bool hasHigherPriority = IsHigherPrioritySelection(
                    selectionStates.AsSpan(), dpSelectionStates[capacity], bestState,
                    candidateMembership, currentMembership);
                if (allExecutionFeesEqual
                    ? hasHigherPriority
                    : dpFees[capacity] > bestFee || dpFees[capacity] == bestFee && hasHigherPriority)
                {
                    bestState = dpSelectionStates[capacity];
                    bestFee = dpFees[capacity];
                }
            }

            using ArrayPoolList<int> chosenCandidateIndexes = new(leftoverCapacity);
            for (int state = bestState; state >= 0; state = selectionStates[state].PreviousState)
            {
                chosenCandidateIndexes.Add(selectionStates[state].CandidateIndex);
            }

            chosenCandidateIndexes.AsSpan().Sort();
            foreach (int candidateIndex in chosenCandidateIndexes.AsSpan())
            {
                selectedBlobTxs.Add(candidateTxs[candidateIndex].tx);
            }
        }

        private static bool IsHigherPrioritySelection(
            ReadOnlySpan<(int CandidateIndex, int PreviousState)> states,
            int candidateState,
            int currentState,
            Span<bool> candidateMembership,
            Span<bool> currentMembership)
        {
            candidateMembership.Clear();
            currentMembership.Clear();
            for (int state = candidateState; state >= 0; state = states[state].PreviousState)
            {
                candidateMembership[states[state].CandidateIndex] = true;
            }

            for (int state = currentState; state >= 0; state = states[state].PreviousState)
            {
                currentMembership[states[state].CandidateIndex] = true;
            }

            // Candidate indices follow producer-priority order. At the first index where
            // selections differ, the selection containing that earlier candidate wins.
            for (int index = 0; index < candidateMembership.Length; index++)
            {
                if (candidateMembership[index] != currentMembership[index]) return candidateMembership[index];
            }

            return false;
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

        protected virtual IEnumerable<Transaction> GetOrderedTransactions(IDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, ulong gasLimit) =>
            Order(pendingTransactions, comparer, filter, gasLimit);

        private static IEnumerable<(Transaction tx, ulong blobChain)> GetOrderedBlobTransactions(IDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, ulong maxBlobs = 0ul) =>
            OrderCore(pendingTransactions, comparer, static tx => (ulong)tx.GetBlobCount(), filter, maxBlobs);

        protected virtual IComparer<Transaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext)
            => _transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        internal static IEnumerable<Transaction> Order(IDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer, Func<Transaction, bool> filter, ulong gasLimit) =>
            OrderCore(pendingTransactions, comparer, static tx => tx.BlockGasUsed, filter, gasLimit).Select(static tx => tx.tx);

        private static IEnumerable<(Transaction tx, ulong resource)> OrderCore(
            IDictionary<AddressAsKey, Transaction[]> pendingTransactions,
            IComparer<Transaction> comparer,
            Func<Transaction, ulong> resourceSelector,
            Func<Transaction, bool> filter,
            ulong resourceLimit)
        {
            using ArrayPoolList<IEnumerator<Transaction>> bySenderEnumerators = pendingTransactions
                .Select<KeyValuePair<AddressAsKey, Transaction[]>, IEnumerable<Transaction>>(static g => g.Value)
                .Select(static g => g.GetEnumerator())
                .ToPooledList(pendingTransactions.Count);

            try
            {
                DictionarySortedSet<Transaction, (IEnumerator<Transaction>, ulong)> transactions = SortEnumerators(bySenderEnumerators, comparer);

                while (transactions.Count > 0)
                {
                    (Transaction candidateTx, (IEnumerator<Transaction> enumerator, ulong resourceChain)) = transactions.Min;

                    transactions.Remove(candidateTx);

                    ulong totalResource = resourceChain + resourceSelector(candidateTx);
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

        private static DictionarySortedSet<Transaction, (IEnumerator<Transaction>, ulong)> SortEnumerators(ArrayPoolList<IEnumerator<Transaction>> bySenderEnumerators, IComparer<Transaction> comparerWithIdentity)
        {
            DictionarySortedSet<Transaction, (IEnumerator<Transaction>, ulong)> transactions = new(comparerWithIdentity);

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

    }
}
