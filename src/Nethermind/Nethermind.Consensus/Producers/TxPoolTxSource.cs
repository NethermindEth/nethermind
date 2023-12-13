// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public class TxPoolTxSource : ITxSource
    {
        private readonly ITxPool _transactionPool;
        private readonly ITransactionComparerProvider _transactionComparerProvider;
        private readonly ITxFilterPipeline _txFilterPipeline;
        private readonly ISpecProvider _specProvider;
        protected readonly ILogger _logger;
        private readonly IEip4844Config _eip4844Config;

        public TxPoolTxSource(
            ITxPool? transactionPool,
            ISpecProvider? specProvider,
            ITransactionComparerProvider? transactionComparerProvider,
            ILogManager? logManager,
            ITxFilterPipeline? txFilterPipeline,
            IEip4844Config? eip4844ConstantsProvider = null)
        {
            _transactionPool = transactionPool ?? throw new ArgumentNullException(nameof(transactionPool));
            _transactionComparerProvider = transactionComparerProvider ?? throw new ArgumentNullException(nameof(transactionComparerProvider));
            _txFilterPipeline = txFilterPipeline ?? throw new ArgumentNullException(nameof(txFilterPipeline));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger<TxPoolTxSource>() ?? throw new ArgumentNullException(nameof(logManager));
            _eip4844Config = eip4844ConstantsProvider ?? ConstantEip4844Config.Instance;
        }

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
        {
            long blockNumber = parent.Number + 1;
            IReleaseSpec spec = _specProvider.GetSpec(parent);
            UInt256 baseFee = BaseFeeCalculator.Calculate(parent, spec);
            IDictionary<Address, Transaction[]> pendingTransactions = _transactionPool.GetPendingTransactionsBySender();
            IDictionary<Address, Transaction[]> pendingBlobTransactionsEquivalences = _transactionPool.GetPendingLightBlobTransactionsBySender();
            IComparer<Transaction> comparer = GetComparer(parent, new BlockPreparationContext(baseFee, blockNumber))
                .ThenBy(ByHashTxComparer.Instance); // in order to sort properly and not lose transactions we need to differentiate on their identity which provided comparer might not be doing

            IEnumerable<Transaction> transactions = GetOrderedTransactions(pendingTransactions, comparer);
            IEnumerable<Transaction> blobTransactions = GetOrderedTransactions(pendingBlobTransactionsEquivalences, comparer);
            if (_logger.IsDebug) _logger.Debug($"Collecting pending transactions at block gas limit {gasLimit}.");

            int checkedTransactions = 0;
            int selectedTransactions = 0;
            using ArrayPoolList<Transaction> selectedBlobTxs = new(_eip4844Config.GetMaxBlobsPerBlock());

            SelectBlobTransactions(blobTransactions, parent, spec, selectedBlobTxs);

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

        private IEnumerable<Transaction> PickBlobTxsBetterThanCurrentTx(ArrayPoolList<Transaction> selectedBlobTxs, Transaction tx, IComparer<Transaction> comparer)
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

        private void SelectBlobTransactions(IEnumerable<Transaction> blobTransactions, BlockHeader parent, IReleaseSpec spec, ArrayPoolList<Transaction> selectedBlobTxs)
        {
            int checkedBlobTransactions = 0;
            int selectedBlobTransactions = 0;
            UInt256 blobGasCounter = 0;
            UInt256 blobGasPrice = UInt256.Zero;

            foreach (Transaction blobTx in blobTransactions)
            {
                if (blobGasCounter >= _eip4844Config.MaxBlobGasPerBlock)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, no more blob space. Block already have {blobGasCounter} blob gas which is max value allowed.");
                    break;
                }

                checkedBlobTransactions++;

                ulong txBlobGas = (ulong)(blobTx.BlobVersionedHashes?.Length ?? 0) * _eip4844Config.GasPerBlob;
                if (txBlobGas > _eip4844Config.MaxBlobGasPerBlock - blobGasCounter)
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, not enough blob space.");
                    continue;
                }

                if (blobGasPrice.IsZero && !TryUpdateBlobGasPrice(blobTx, parent, spec, out blobGasPrice))
                {
                    if (_logger.IsTrace) _logger.Trace($"Declining {blobTx.ToShortString()}, failed to get full version of this blob tx from TxPool.");
                    continue;
                }

                if (blobGasPrice > blobTx.MaxFeePerBlobGas)
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

                blobGasCounter += txBlobGas;
                if (_logger.IsTrace) _logger.Trace($"Selected shard blob tx {fullBlobTx.ToShortString()} to be potentially included in block, total blob gas included: {blobGasCounter}.");

                selectedBlobTransactions++;
                selectedBlobTxs.Add(fullBlobTx);
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

        private bool TryUpdateBlobGasPrice(Transaction lightBlobTx, BlockHeader parent, IReleaseSpec spec, out UInt256 blobGasPrice)
        {
            ulong? excessDataGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
            if (excessDataGas is null)
            {
                if (_logger.IsTrace) _logger.Trace($"Declining {lightBlobTx.ToShortString()}, the specification is not configured to handle shard blob transactions.");
                blobGasPrice = UInt256.Zero;
                return false;
            }

            if (!BlobGasCalculator.TryCalculateBlobGasPricePerUnit(excessDataGas.Value, out blobGasPrice))
            {
                if (_logger.IsTrace) _logger.Trace($"Declining {lightBlobTx.ToShortString()}, failed to calculate data gas price.");
                blobGasPrice = UInt256.Zero;
                return false;
            }

            return true;
        }

        protected virtual IEnumerable<Transaction> GetOrderedTransactions(IDictionary<Address, Transaction[]> pendingTransactions, IComparer<Transaction> comparer) =>
            Order(pendingTransactions, comparer);

        protected virtual IComparer<Transaction> GetComparer(BlockHeader parent, BlockPreparationContext blockPreparationContext)
            => _transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContext);

        internal static IEnumerable<Transaction> Order(IDictionary<Address, Transaction[]> pendingTransactions, IComparer<Transaction> comparerWithIdentity)
        {
            IEnumerator<Transaction>[] bySenderEnumerators = pendingTransactions
                .Select<KeyValuePair<Address, Transaction[]>, IEnumerable<Transaction>>(g => g.Value)
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
