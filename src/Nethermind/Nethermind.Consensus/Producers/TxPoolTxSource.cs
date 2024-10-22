// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

[assembly: InternalsVisibleTo("Nethermind.AuRa.Test")]

namespace Nethermind.Consensus.Producers
{
    public class TxPoolTxSource : ITxSource, ITxSourceNotifier
    {
        private readonly ITxPool _transactionPool;
        private readonly ITransactionComparerProvider _transactionComparerProvider;
        private readonly ITxFilterPipeline _txFilterPipeline;
        private readonly ISpecProvider _specProvider;
        private readonly IStateReader? _stateReader;
        protected readonly ILogger _logger;
        private readonly IEip4844Config _eip4844Config;

        public event EventHandler<TxEventArgs> NewPendingTransactions;

        public TxPoolTxSource(
            ITxPool? transactionPool,
            ISpecProvider? specProvider,
            ITransactionComparerProvider? transactionComparerProvider,
            ILogManager? logManager,
            ITxFilterPipeline? txFilterPipeline,
            IStateReader? stateReader = null,
            IEip4844Config? eip4844ConstantsProvider = null)
        {
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _transactionComparerProvider = transactionComparerProvider ?? throw new ArgumentNullException(nameof(transactionComparerProvider));
            _txFilterPipeline = txFilterPipeline ?? throw new ArgumentNullException(nameof(txFilterPipeline));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger<TxPoolTxSource>() ?? throw new ArgumentNullException(nameof(logManager));
            _stateReader = stateReader;
            _eip4844Config = eip4844ConstantsProvider ?? ConstantEip4844Config.Instance;
            _transactionPool.NewPending += TransactionPool_NewPending;
        }

        private void TransactionPool_NewPending(object? sender, TxEventArgs e)
            => NewPendingTransactions?.Invoke(sender, e);

        public bool IsInterestingTx(Transaction tx, BlockHeader parent)
            => _txFilterPipeline.Execute(tx, parent);

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, CancellationToken token = default)
        {
            IReleaseSpec spec = _specProvider.GetSpec(parent);
            UInt256 baseFee = BaseFeeCalculator.Calculate(parent, spec);

            // Get snapshots of pending regular transactions and pending blob transactions from the transaction pool
            Transaction[] transactionSnapshot = _transactionPool.GetPendingTransactions();
            Transaction[] blobTransactionSnapshot = _transactionPool.GetPendingBlobTransactions();

            using ArrayPoolList<Transaction> selectedTxs = new(transactionSnapshot.Length + blobTransactionSnapshot.Length);

            // Add valid regular transactions to the selected list
            selectedTxs.AddRange(transactionSnapshot
                .Where(tx =>
                    tx.SenderAddress is not null && // Ensure the transaction has a sender address
                    _txFilterPipeline.Execute(tx, parent) && // Pass the transaction through the filter pipeline
                    !tx.IsAboveInitCode(spec)) // Exclude transactions that exceed the init code size limit
                .DistinctBy((tx) => tx, ByHashTxComparer.Instance)); // Remove duplicate transactions based on their hash

            // Add valid blob transactions to the selected list
            selectedTxs.AddRange(blobTransactionSnapshot
                .Where(tx =>
                    tx.SenderAddress is not null && // Ensure the transaction has a sender address
                    _txFilterPipeline.Execute(tx, parent) && // Pass the transaction through the filter pipeline
                    !tx.IsAboveInitCode(spec)) // Exclude transactions that exceed the init code size limit
                .Select(tx => TryGetFullBlobTx(tx, out Transaction fullBlobTx) ? fullBlobTx : null) // Ensure the transaction has the blob
                .Where(tx => tx is not null)
                .DistinctBy((tx) => tx, ByHashTxComparer.Instance)); // Remove duplicate transactions based on their hash

            int checkedTransactions = selectedTxs.Count;

            long blockNumber = parent.Number + 1;
            // Obtain a transaction comparer based on the block context (e.g., gas price, fees)
            IComparer<Transaction> comparer = GetComparer(parent, new BlockPreparationContext(baseFee, blockNumber))
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not lose transactions we need to differentiate on their identity which provided comparer might not be doing

            // Sort the selected transactions using the comparer
            selectedTxs.AsSpan().Sort(comparer);

            // Sort transactions from the same sender by their nonce in ascending order
            // Otherwise txs will be invalidated by not being in nonce order
            SortMultiSendersByNonce(selectedTxs, parent, spec, token);
            // Remove any transactions that have been invalidated or are no longer valid after sorting
            RemoveBlankedTransactions(selectedTxs);

            // Re-sort transactions to maintain the correct order, ensuring transactions from the same sender remain in nonce order
            // As sorting by nonce may have put txs with lower priorty earlier
            selectedTxs.AsSpan().Sort((tx0, tx1) =>
                tx0.SenderAddress == tx1.SenderAddress ?
                0 :  // If transactions are from the same sender, keep their current order
                comparer.Compare(tx0, tx1)); // Otherwise, sort based on the main comparer

            // Remove blob transactions that cannot be included in the block (e.g. due to block limit constraints)
            RemoveBlobTransactions(selectedTxs.AsSpan(), parent, spec);
            // Remove any additional invalidated transactions after blob transaction removal
            RemoveBlankedTransactions(selectedTxs);

            // Convert the list of selected transactions to an array for inclusion in the block
            Transaction[] potentialTxs = selectedTxs.ToArray();

            if (_logger.IsDebug) _logger.Debug($"Potentially selected {potentialTxs.Length} out of {checkedTransactions} pending transactions checked.");

            // Return the selected transactions unless the operation was cancelled
            return token.IsCancellationRequested ? Array.Empty<Transaction>() : potentialTxs;
        }

        private static void RemoveBlankedTransactions(ArrayPoolList<Transaction> selectedTxs)
        {
            int count = selectedTxs.Count;
            for (int i = 0; i < count; i++)
            {
                if (selectedTxs[i] is null)
                {
                    selectedTxs.RemoveAt(i);
                    count--;
                    i--;
                }
            }
        }

        /// <summary>
        /// Sorts transactions from multiple senders by nonce, ensuring that transactions from the same sender
        /// are in the correct nonce sequence without gaps. Removes any transactions that have invalid nonces
        /// or create nonce gaps, along with subsequent transactions from the same sender.
        /// </summary>
        /// <param name="selectedTxs">The list of selected transactions to process.</param>
        /// <param name="parent">The parent block header.</param>
        /// <param name="spec">The release specification.</param>
        /// <param name="token">Cancellation token to handle task cancellation.</param>
        private void SortMultiSendersByNonce(ArrayPoolList<Transaction> selectedTxs, BlockHeader parent, IReleaseSpec spec, CancellationToken token)
        {
            // Group transactions by their sender address, including their original indices in selectedTxs
            // Only process senders who have submitted more than one transaction
            var senderGroups = selectedTxs
                .Select((t, index) => (t, index))
                .GroupBy(ti => ti.t.SenderAddress)
                .Where(senderGroup => senderGroup.Count() > 1);

            bool eip1559Enabled = spec.IsEip1559Enabled;

            // Process each group of transactions from the same sender
            foreach (var group in senderGroups)
            {
                // Order the transactions from this sender by nonce
                var order = group.OrderBy(ti => ti.t.Nonce)
                    .Select(ti => ti.t).ToArray();
                // Get the original indices of these transactions in selectedTxs, in order of appearance
                var current = group.OrderBy(ti => ti.index)
                    .Select(ti => ti.index).ToArray();

                // Check for cancellation before proceeding to access the database
                if (token.IsCancellationRequested) return;

                // Retrieve the sender's account to get the first expected nonce
                UInt256 expectedNonce;
                if (_stateReader is not null && _stateReader.TryGetAccount(parent.StateRoot, group.Key, out AccountStruct account))
                {
                    expectedNonce = account.Nonce;
                }
                else
                {
                    expectedNonce = order[0].Nonce;
                }

                bool removeTx = false;
                // Iterate over the transactions to validate nonce sequence and remove invalid ones
                for (int index = 0; index < current.Length; index++)
                {
                    // Get tx next position for this senders txs in overall tx array
                    int offset = current[index];
                    Transaction? tx = null;
                    if (!removeTx)
                    {
                        // Get the next transaction in nonce order
                        tx = order[index];
                        // Check if the transaction's nonce matches the expected nonce
                        if (expectedNonce != tx.Nonce)
                        {
                            // Nonce mismatch: remove this transaction and all subsequent ones from this sender
                            tx = null;
                            // Remove all following txs
                            removeTx = true;
                        }
                        else
                        {
                            // Nonce is as expected; increment expected nonce for the next transaction
                            expectedNonce++;
                        }
                    }

                    // Update the transaction in selectedTxs at its original position
                    // If tx is null, the transaction is effectively removed
                    selectedTxs[offset] = tx;
                }
            }
        }

        private void RemoveBlobTransactions(Span<Transaction> selectedTxs, BlockHeader parent, IReleaseSpec spec)
        {
            int checkedBlobTransactions = 0;
            int selectedBlobTransactions = 0;
            UInt256 blobGasCounter = 0;
            UInt256 feePerBlobGas = UInt256.Zero;

            HashSet<AddressAsKey> sendersRemoved = new();
            bool blankAllBlobTxs = false;
            for (int i = 0; i < selectedTxs.Length; i++)
            {
                Transaction blobTx = selectedTxs[i];
                if (sendersRemoved.Contains(blobTx.SenderAddress))
                {
                    selectedTxs[i] = null;
                    continue;
                }
                if (blobTx.Type != TxType.Blob)
                {
                    // Not blob
                    continue;
                }

                checkedBlobTransactions++;

                if (blobGasCounter >= _eip4844Config.MaxBlobGasPerBlock)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, no more blob space. Block already have {blobGasCounter} blob gas which is max value allowed.");
                    blankAllBlobTxs = true;
                }
                if (blankAllBlobTxs)
                {
                    sendersRemoved.Add(blobTx.SenderAddress);
                    selectedTxs[i] = null;
                    continue;
                }

                ulong txBlobGas = (ulong)(blobTx.BlobVersionedHashes?.Length ?? 0) * _eip4844Config.GasPerBlob;
                if (txBlobGas > _eip4844Config.MaxBlobGasPerBlock - blobGasCounter)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, not enough blob space.");

                    sendersRemoved.Add(blobTx.SenderAddress);
                    selectedTxs[i] = null;
                    continue;
                }

                if (feePerBlobGas.IsZero && !TryUpdateFeePerBlobGas(blobTx, parent, spec, out feePerBlobGas))
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, failed to get full version of this blob tx from TxPool.");

                    sendersRemoved.Add(blobTx.SenderAddress);
                    selectedTxs[i] = null;
                    continue;
                }

                if (feePerBlobGas > blobTx.MaxFeePerBlobGas)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, data gas fee is too low.");

                    sendersRemoved.Add(blobTx.SenderAddress);
                    selectedTxs[i] = null;
                    continue;
                }

                bool success = _txFilterPipeline.Execute(blobTx, parent);
                if (!success)
                {

                    sendersRemoved.Add(blobTx.SenderAddress);
                    selectedTxs[i] = null;
                    continue;
                }

                blobGasCounter += txBlobGas;
                if (_logger.IsTrace) _logger.Trace($"Selected shard blob tx {blobTx.ToShortString()} to be potentially included in block, total blob gas included: {blobGasCounter}.");

                selectedBlobTransactions++;
            }

            if (_logger.IsDebug) _logger.Debug($"Potentially selected {selectedBlobTransactions} out of {checkedBlobTransactions} pending blob transactions checked.");
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

            if (!BlobGasCalculator.TryCalculateFeePerBlobGas(excessDataGas.Value, out feePerBlobGas))
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
                .Select<KeyValuePair<AddressAsKey, Transaction[]>, IEnumerable<Transaction>>(g => g.Value)
                .Select(g => g.GetEnumerator())
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

        public override string ToString() => $"{nameof(TxPoolTxSource)}";
    }
}
