// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.State;
using Nethermind.TxPool.Comparison;
using TxAction = Nethermind.Consensus.Processing.BlockProcessor.TxAction;

namespace Nethermind.Mev
{
    // TODO: why there is anything special needed?
    // this looks super complex, hmm, is it needed?
    public class MevBlockProductionTransactionsExecutor : BlockProcessor.BlockProductionTransactionsExecutor
    {
        private readonly IWorldState _stateProvider;

        public MevBlockProductionTransactionsExecutor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
            ISpecProvider specProvider,
            ILogManager logManager) :
            this(
                readOnlyTxProcessingEnv.TransactionProcessor,
                readOnlyTxProcessingEnv.StateProvider,
                specProvider,
                logManager)
        {
        }

        private MevBlockProductionTransactionsExecutor(
            ITransactionProcessor transactionProcessor,
            IWorldState stateProvider,
            ISpecProvider specProvider,
            ILogManager logManager)
            : base(transactionProcessor, stateProvider, specProvider, logManager)
        {
            _stateProvider = stateProvider;
        }

        public override TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockExecutionTracer receiptsTracer, IReleaseSpec spec)
        {
            IEnumerable<Transaction> transactions = GetTransactions(block);
            LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
            List<BundleTransaction> bundleTransactions = new();
            Keccak? bundleHash = null;

            foreach (Transaction currentTx in transactions)
            {
                // if we don't accumulate bundle yet
                if (bundleHash is null)
                {
                    // and we see a bundle transaction
                    if (currentTx is BundleTransaction bundleTransaction)
                    {
                        // start accumulating the bundle6
                        bundleTransactions.Add(bundleTransaction);
                        bundleHash = bundleTransaction.BundleHash;
                    }
                    else
                    {
                        // otherwise process transaction as usual
                        TxAction action = ProcessTransaction(block, currentTx, transactionsInBlock.Count, receiptsTracer, processingOptions, transactionsInBlock);
                        if (action == TxAction.Stop) break;
                    }
                }
                // if we are accumulating bundle
                else
                {
                    // if we see a bundle transaction
                    if (currentTx is BundleTransaction bundleTransaction)
                    {
                        // if its from same bundle
                        if (bundleTransaction.BundleHash == bundleHash)
                        {
                            // keep accumulating the bundle
                            bundleTransactions.Add(bundleTransaction);
                        }
                        // if its from different bundle
                        else
                        {
                            // process accumulated bundle
                            TxAction action = ProcessBundle(block, bundleTransactions, transactionsInBlock, receiptsTracer, processingOptions);
                            if (action == TxAction.Stop) break;

                            // start accumulating new bundle
                            bundleTransactions.Add(bundleTransaction);
                            bundleHash = bundleTransaction.BundleHash;
                        }
                    }
                    // if we see a normal transaction
                    else
                    {
                        // process the bundle and stop accumulating it
                        bundleHash = null;
                        TxAction action = ProcessBundle(block, bundleTransactions, transactionsInBlock, receiptsTracer, processingOptions);
                        if (action == TxAction.Stop) break;

                        // process normal transaction
                        action = ProcessTransaction(block, currentTx, transactionsInBlock.Count, receiptsTracer, processingOptions, transactionsInBlock);
                        if (action == TxAction.Stop) break;
                    }
                }
            }
            // if we ended with accumulated bundle, lets process it
            if (bundleTransactions.Count > 0)
            {
                ProcessBundle(block, bundleTransactions, transactionsInBlock, receiptsTracer, processingOptions);
            }

            _stateProvider.Commit(spec, receiptsTracer);

            SetTransactions(block, transactionsInBlock);
            return receiptsTracer.TxReceipts.ToArray();
        }

        private TxAction ProcessBundle(Block block,
            List<BundleTransaction> bundleTransactions,
            LinkedHashSet<Transaction> transactionsInBlock,
            BlockExecutionTracer receiptsTracer,
            ProcessingOptions processingOptions)
        {

            Snapshot snapshot = _stateProvider.TakeSnapshot();
            int receiptSnapshot = receiptsTracer.TakeSnapshot();
            UInt256 initialBalance = _stateProvider.GetBalance(block.Header.GasBeneficiary!);

            bool CheckFeeNotManipulated()
            {
                UInt256 finalBalance = _stateProvider.GetBalance(block.Header.GasBeneficiary!);
                UInt256 feeReceived = finalBalance - initialBalance;
                UInt256 originalSimulatedGasPrice = bundleTransactions[0].SimulatedBundleFee / bundleTransactions[0].SimulatedBundleGasUsed;
                UInt256 actualGasPrice = feeReceived / (UInt256)receiptsTracer.LastReceipt.GasUsed!;
                return actualGasPrice >= originalSimulatedGasPrice;
            }

            bool bundleSucceeded = bundleTransactions.Count > 0;
            TxAction txAction = TxAction.Skip;
            for (int index = 0; index < bundleTransactions.Count && bundleSucceeded; index++)
            {
                txAction = ProcessBundleTransaction(block, bundleTransactions[index], index, receiptsTracer, processingOptions, transactionsInBlock);
                bundleSucceeded &= txAction == TxAction.Add;

                // if we need to stop on not first tx in the bundle, we actually want to skip the bundle
                txAction = txAction == TxAction.Stop && index != 0 ? TxAction.Skip : txAction;
            }

            if (bundleSucceeded)
            {
                bundleSucceeded &= CheckFeeNotManipulated();
            }

            if (bundleSucceeded)
            {
                for (int index = 0; index < bundleTransactions.Count; index++)
                {
                    BundleTransaction bundleTransaction = bundleTransactions[index];
                    transactionsInBlock.Add(bundleTransaction);
                    int txIndex = receiptSnapshot + index;
                    _transactionProcessed?.Invoke(this, new TxProcessedEventArgs(txIndex, bundleTransaction, receiptsTracer.TxReceipts[txIndex]));
                }
            }
            else
            {
                _stateProvider.Restore(snapshot);
                receiptsTracer.Restore(receiptSnapshot);
                for (int index = 0; index < bundleTransactions.Count; index++)
                {
                    transactionsInBlock.Remove(bundleTransactions[index]);
                }
            }

            bundleTransactions.Clear();

            return txAction;
        }

        private TxAction ProcessBundleTransaction(
            Block block,
            BundleTransaction currentTx,
            int index,
            BlockExecutionTracer receiptsTracer,
            ProcessingOptions processingOptions,
            LinkedHashSet<Transaction> transactionsInBlock)
        {
            TxAction action = ProcessTransaction(block, currentTx, index, receiptsTracer, processingOptions, transactionsInBlock, false);
            if (action == TxAction.Add)
            {
                string? error = receiptsTracer.LastReceipt.Error;
                bool transactionSucceeded = string.IsNullOrEmpty(error) || (error == "revert" && currentTx.CanRevert);
                return transactionSucceeded ? TxAction.Add : TxAction.Skip;
            }
            else
            {
                return action;
            }
        }
    }
}
