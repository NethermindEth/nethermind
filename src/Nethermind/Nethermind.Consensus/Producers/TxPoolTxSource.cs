// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
            IReleaseSpec spec = _specProvider.GetSpec(parent);
            UInt256 baseFee = BaseFeeCalculator.Calculate(parent, spec);
            IDictionary<AddressAsKey, Transaction[]> pendingTransactions = filterSource ?
                _transactionPool.GetPendingTransactionsBySender(filterToReadyTx: true, baseFee) :
                _transactionPool.GetPendingTransactionsBySender();
            IDictionary<AddressAsKey, Transaction[]> pendingBlobTransactionsEquivalences = _transactionPool.GetPendingLightBlobTransactionsBySender();
            IComparer<Transaction> comparer = GetComparer(parent, new BlockPreparationContext(baseFee, blockNumber))
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not lose transactions we need to differentiate on their identity which provided comparer might not be doing

            IEnumerable<Transaction> transactions = GetOrderedTransactions(pendingTransactions, comparer);
            IEnumerable<Transaction> blobTransactions = GetOrderedTransactions(pendingBlobTransactionsEquivalences, comparer);
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

                bool success = _txFilterPipeline.Execute(tx, parent);
                if (!success) continue;

                foreach (Transaction blobTx in PickBlobTxsBetterThanCurrentTx(selectedBlobTxs, tx, comparer))
                {
                    yield return blobTx;
                }

                if (_logger.IsTrace) _logger.Trace($"Selected {tx.ToShortString()} to be potentially included in block.");

                selectedTransactions++;
                yield return tx;
            }

            if (selectedBlobTxs.Count > 0)
            {
                foreach (Transaction blobTx in selectedBlobTxs)
                {
                    yield return blobTx;
                }
            }

            if (_logger.IsDebug) _logger.Debug($"Potentially selected {selectedTransactions} out of {checkedTransactions} pending transactions checked.");
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

        private void SelectBlobTransactions(IEnumerable<Transaction> blobTransactions, BlockHeader parent, IReleaseSpec spec, in UInt256 baseFee, ArrayPoolList<Transaction> selectedBlobTxs)
        {
            int maxBlobsPerBlock = (int)spec.MaxBlobCount;
            int countOfRemainingBlobs = 0;

            ArrayPoolList<Transaction>? candidates = null;
            foreach (Transaction blobTx in blobTransactions)
            {
                int txBlobCount = blobTx.GetBlobCount();
                if (txBlobCount > maxBlobsPerBlock)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, not enough blob space.");
                    continue;
                }

                if (!TryUpdateFeePerBlobGas(blobTx, parent, spec, out UInt256 feePerBlobGas))
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, failed to get full version of this blob tx from TxPool.");
                    continue;
                }

                if (feePerBlobGas > blobTx.MaxFeePerBlobGas)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, data gas fee is too low.");
                    continue;
                }

                bool success = _txFilterPipeline.Execute(blobTx, parent);
                if (!success) continue;

                if (!TryGetFullBlobTx(blobTx, out Transaction fullBlobTx))
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, failed to get full version of this blob tx from TxPool.");
                    continue;
                }

                if (txBlobCount == 1 && candidates is null)
                {
                    selectedBlobTxs.Add(fullBlobTx);
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

                    candidates.Add(fullBlobTx);
                    countOfRemainingBlobs += txBlobCount;
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
                        selectedBlobTxs.Add(tx);
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
        /// The chosen transactions are appended to <paramref name="finalSelectedBlobTxs"/>.
        /// </summary>
        /// <param name="candidateTxs">A list of candidate blob transactions.</param>
        /// <param name="leftoverCapacity">The maximum remaining blob capacity available.</param>
        /// <param name="selectedBlobTxs">
        /// A collection to which the chosen transactions will be added.
        /// Existing entries remain untouched; chosen ones are appended at the end.
        /// </param>
        private static void ChooseBestBlobTransactions(
            ArrayPoolList<Transaction> candidateTxs,
            int leftoverCapacity,
            in UInt256 baseFee,
            ArrayPoolList<Transaction> selectedBlobTxs)
        {
            int size = leftoverCapacity + 1;
            // The maximum total fee achievable with capacity
            using ArrayPoolList<ulong> dpFeesPooled = new(capacity: size, count: size);
            Span<ulong> dpFees = dpFeesPooled.AsSpan();

            using ArrayPoolBitMap isChosen = new(candidateTxs.Count * size);

            // Build up the DP table to find the maximum total fee for each capacity.
            // Outer loop: go through each transaction (1-based index).
            // Inner loop: iterate capacity in descending order to avoid overwriting data needed for the calculation.
            for (int i = 0; i < candidateTxs.Count; i++)
            {
                Transaction tx = candidateTxs[i];
                if (!tx.TryCalculatePremiumPerGas(baseFee, out UInt256 premiumPerGas))
                {
                    continue;
                }
                int blobCount = tx.GetBlobCount();
                // Use actual gas used when available as the tx may be using over-estimated gaslimit
                ulong feeValue = (ulong)premiumPerGas * (ulong)(tx.SpentGas);

                // Iterate backward from maxBlobCapacity down to blobCount
                // so we only compute for valid capacities that can fit this transaction.
                for (int capacity = leftoverCapacity; capacity >= blobCount; capacity--)
                {
                    // If we can fit this item in capacity, see if it improves dpFees[capacity]
                    ulong candidateFee = dpFees[capacity - blobCount] + feeValue;
                    if (candidateFee > dpFees[capacity])
                    {
                        dpFees[capacity] = candidateFee;
                        isChosen[i * size + capacity] = true;
                    }
                }
            }

            int start = selectedBlobTxs.Count;
            // Backtrack through 'choices' to find which transactions were actually chosen.
            int remainingCapacity = leftoverCapacity;
            for (int i = candidateTxs.Count - 1; i >= 0; i--)
            {
                if (isChosen[i * size + remainingCapacity])
                {
                    Transaction tx = candidateTxs[i];
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

        private bool TryUpdateFeePerBlobGas(Transaction lightBlobTx, BlockHeader parent, IReleaseSpec spec, out UInt256 feePerBlobGas)
        {
            ulong? excessDataGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
            if (excessDataGas is null)
            {
                if (_logger.IsTrace) _logger.Trace($"Declining {lightBlobTx.ToShortString()}, the specification is not configured to handle shard blob transactions.");
                feePerBlobGas = UInt256.Zero;
                return false;
            }

            if (!BlobGasCalculator.TryCalculateFeePerBlobGas(excessDataGas.Value, spec.BlobBaseFeeUpdateFraction, out feePerBlobGas))
            {
                if (_logger.IsTrace) _logger.Trace($"Declining {lightBlobTx.ToShortString()}, failed to calculate data gas price.");
                feePerBlobGas = UInt256.Zero;
                return false;
            }

            return true;
        }

        protected virtual IEnumerable<Transaction> GetOrderedTransactions(IDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparer) =>
            Order(pendingTransactions, comparer);

        protected virtual IComparer<Transaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext)
            => _transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        internal static IEnumerable<Transaction> Order(IDictionary<AddressAsKey, Transaction[]> pendingTransactions, IComparer<Transaction> comparerWithIdentity)
        {
            IEnumerator<Transaction>[] bySenderEnumerators = pendingTransactions
                .Select<KeyValuePair<AddressAsKey, Transaction[]>, IEnumerable<Transaction>>(static g => g.Value)
                .Select(static g => g.GetEnumerator())
                .ToArray();

            try
            {
                // we create a sorted list of head of each group of transactions. From:
                // A -> N0_P3, N1_P1, N1_P0, N3_P5...
                // B -> N4_P4, N5_P3, N6_P3...
                // We construct [N4_P4 (B), N0_P3 (A)] in sorted order by priority
                DictionarySortedSet<Transaction, IEnumerator<Transaction>> transactions = new(comparerWithIdentity);

                for (int i = 0; i < bySenderEnumerators.Length; i++)
                {
                    IEnumerator<Transaction> enumerator = bySenderEnumerators[i];
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }
                }

                // while there are still unreturned transactions
                while (transactions.Count > 0)
                {
                    // we take first transaction from sorting order, on first call: N4_P4 from B
                    (Transaction tx, IEnumerator<Transaction> enumerator) = transactions.Min;

                    // we replace it by next transaction from same sender, on first call N5_P3 from B
                    transactions.Remove(tx);
                    if (enumerator.MoveNext())
                    {
                        transactions.Add(enumerator.Current!, enumerator);
                    }

                    // we return transactions in lazy manner, no need to sort more than will be taken into block
                    yield return tx;
                }
            }
            finally
            {
                // disposing enumerators
                for (int i = 0; i < bySenderEnumerators.Length; i++)
                {
                    bySenderEnumerators[i].Dispose();
                }
            }
        }

        public bool SupportsBlobs => _transactionPool.SupportsBlobs;

        public override string ToString() => $"{nameof(TxPoolTxSource)}";

        private class ArrayPoolBitMap : IDisposable
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
