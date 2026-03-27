// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockProductionTransactionsExecutor(
            ITransactionProcessorAdapter transactionProcessor,
            IWorldState stateProvider,
            ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ISpecProvider specProvider,
            IBlockhashProvider blockHashProvider,
            ICodeInfoRepository codeInfoRepository,
            IBlockProductionTransactionPicker txPicker,
            ILogManager logManager)
            : IBlockProductionTransactionsExecutor
        {
            public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
            // private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;
            private readonly ILogger _logger = logManager.GetClassLogger();
            private BlockExecutionContext _blockExecutionContext;
            private BlockAccessList[] _intermediateBlockAccessLists;
            private ITransactionProcessorAdapter[] _transactionProcessorAdapters;
            private ITransactionProcessor[] _transactionProcessors;
            private TracedAccessWorldState[] _parallelWorldState;
            private TxReceipt[] _txReceipts;

            protected EventHandler<TxProcessedEventArgs>? _transactionProcessed;

            event EventHandler<AddingTxEventArgs>? IBlockProductionTransactionsExecutor.AddingTransaction
            {
                add => txPicker.AddingTransaction += value;
                remove => txPicker.AddingTransaction -= value;
            }

            public void SetGasUsed(long gasUsed) { }

            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            {
                transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
                _blockExecutionContext = blockExecutionContext;
            }

            private (ExecuteTransactionProcessorAdapter, TransactionProcessor<EthereumGasPolicy>, TracedAccessWorldState) CreateTransactionProcessor(Block block, int balIndex, bool isBuildingBlock)
            {
                VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
                TracedAccessWorldState parallelWorldState = new(stateProvider);
                TransactionProcessor<EthereumGasPolicy> transactionProcessor = new(blobBaseFeeCalculator, specProvider, parallelWorldState, virtualMachine, codeInfoRepository, logManager);
                ExecuteTransactionProcessorAdapter transactionProcessorAdapter = new(transactionProcessor);
                transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);

                return (transactionProcessorAdapter, transactionProcessor, parallelWorldState);
            }

            public void Setup(Block block, ProcessingOptions processingOptions)
            {
                int len = block.Transactions.Length;
                _transactionProcessorAdapters = new ITransactionProcessorAdapter[len + 2];
                _transactionProcessors = new ITransactionProcessor[len + 2];
                _parallelWorldState = new TracedAccessWorldState[len + 2];
                _intermediateBlockAccessLists = new BlockAccessList[len + 2];

                for (int i = 0; i < len + 2; i++)
                {
                    BlockAccessList bal = new()
                    {
                        Index = i
                    };
                    _intermediateBlockAccessLists[i] = bal;

                    (ExecuteTransactionProcessorAdapter transactionProcessorAdapter, TransactionProcessor<EthereumGasPolicy> transactionProcessor, TracedAccessWorldState parallelWorldState) = CreateTransactionProcessor(block, i, processingOptions.ContainsFlag(ProcessingOptions.ProducingBlock));
                    _transactionProcessors[i] = transactionProcessor;
                    _transactionProcessorAdapters[i] = transactionProcessorAdapter;
                    _parallelWorldState[i] = parallelWorldState;
                }

            }

            public virtual TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions,
                BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
            {

                // VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
                // ParallelWorldState parallelWorldState = new(stateProvider, specProvider, blocksConfig, -1, block, new(), GeneratedBlockAccessList, new(logManager), processingOptions.ContainsFlag(ProcessingOptions.ProducingBlock));
                // TransactionProcessor<EthereumGasPolicy> transactionProcessor = new(blobBaseFeeCalculator, specProvider, parallelWorldState, virtualMachine, codeInfoRepository, logManager);
                // ExecuteTransactionProcessorAdapter transactionProcessorAdapter = new(transactionProcessor);
                // transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);

                // int len = block.Transactions.Length;
                // _transactionProcessorAdapters = new ITransactionProcessorAdapter[len + 2];
                // _transactionProcessors = new ITransactionProcessor[len + 2];
                // _parallelWorldState = new ParallelWorldState[len + 2];
                // _intermediateBlockAccessLists = new BlockAccessList[len + 2];

                // for (int j = 0; j < len + 2; j++)
                // {
                //     BlockAccessList bal = new()
                //     {
                //         Index = j
                //     };
                //     _intermediateBlockAccessLists[j] = bal;

                //     (ExecuteTransactionProcessorAdapter transactionProcessorAdapter, TransactionProcessor<EthereumGasPolicy> transactionProcessor, ParallelWorldState parallelWorldState) = CreateTransactionProcessor(block, j, processingOptions.ContainsFlag(ProcessingOptions.ProducingBlock));
                //     _transactionProcessors[j] = transactionProcessor;
                //     _transactionProcessorAdapters[j] = transactionProcessorAdapter;
                //     _parallelWorldState[j] = parallelWorldState;
                // }

                MergeIntermediateBalsUpTo(0, _intermediateBlockAccessLists);

                // We start with high number as don't want to resize too much
                const int defaultTxCount = 512;

                BlockToProduce? blockToProduce = block as BlockToProduce;

                // Don't use blockToProduce.Transactions.Count() as that would fully enumerate which is expensive
                int txCount = blockToProduce is not null ? defaultTxCount : block.Transactions.Length;
                IEnumerable<Transaction> transactions = blockToProduce?.Transactions ?? block.Transactions;

                using ArrayPoolListRef<Transaction> includedTx = new(txCount);

                HashSet<Transaction> consideredTx = new(ByHashTxComparer.Instance);
                int i = 0;
                foreach (Transaction currentTx in transactions)
                {
                    // Check if we have gone over time or the payload has been requested
                    if (token.IsCancellationRequested) break;

                    TxAction action = ProcessTransaction(_transactionProcessorAdapters[i + 1], block, currentTx, i++, receiptsTracer, processingOptions, consideredTx);
                    if (action == TxAction.Stop) break;

                    consideredTx.Add(currentTx);
                    if (action == TxAction.Add)
                    {
                        includedTx.Add(currentTx);
                        if (blockToProduce is not null)
                        {
                            blockToProduce.TxByteLength += currentTx.GetLength(false);
                        }
                    }
                }
                // GeneratedBlockAccessList.IncrementBlockAccessIndex();

                block.Header.TxRoot = TxTrie.CalculateRoot(includedTx.AsSpan());
                if (blockToProduce is not null)
                {
                    blockToProduce.Transactions = includedTx.ToArray();
                }
                _txReceipts = receiptsTracer.TxReceipts.ToArray();
                
                return _txReceipts;
            }

            public void StoreBeaconRoot(Block block, IReleaseSpec spec)
            {
                new BeaconBlockRootHandler(_transactionProcessors[0], _parallelWorldState[0]).StoreBeaconRoot(block, spec, NullTxTracer.Instance);
            }

            public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec)
            {
                new BlockhashStore(_parallelWorldState[0]).ApplyBlockhashStateChanges(header, spec);
            }

            public void ProcessWithdrawals(Block block, IReleaseSpec spec)
            {
                new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(_parallelWorldState[^1], logManager)).ProcessWithdrawals(block, spec);
            }

            public void ProcessExecutionRequests(Block block, IReleaseSpec spec)
            {
                new ExecutionRequestsProcessor(_transactionProcessors[^1]).ProcessExecutionRequests(block, _parallelWorldState[^1], _txReceipts, spec);
            }

            public void SetBlockAccessList(Block block, IReleaseSpec spec)
            {
                if (!spec.BlockLevelAccessListsEnabled)
                {
                    return;
                }

                if (block.IsGenesis)
                {
                    block.Header.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
                }
                else
                {
                    MergeIntermediateBalsUpTo((ushort)(block.Transactions.Length + 1), _intermediateBlockAccessLists);
                    block.GeneratedBlockAccessList = GeneratedBlockAccessList;
                    block.EncodedBlockAccessList = Rlp.Encode(GeneratedBlockAccessList).Bytes;
                    block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
                }
            }

            private void MergeIntermediateBalsUpTo(ushort index, BlockAccessList[] intermediateBlockAccessLists)
            {
                if (index == 0)
                {
                    GeneratedBlockAccessList = intermediateBlockAccessLists[0];
                }
                else
                {
                    GeneratedBlockAccessList.Merge(intermediateBlockAccessLists[index]);
                }
            }

            private TxAction ProcessTransaction(
                ITransactionProcessorAdapter transactionProcessor,
                Block block,
                Transaction currentTx,
                int index,
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions,
                HashSet<Transaction> transactionsInBlock)
            {
                AddingTxEventArgs args = txPicker.CanAddTransaction(block, currentTx, transactionsInBlock, stateProvider);

                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsDebug) DebugSkipReason(currentTx, args);
                }
                else
                {
                    // GeneratedBlockAccessList.IncrementBlockAccessIndex();
                    TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);

                    if (result)
                    {
                        MergeIntermediateBalsUpTo((ushort)(index + 1), _intermediateBlockAccessLists);
                        _transactionProcessed?.Invoke(this,
                            new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
                    }
                    else
                    {
                        // GeneratedBlockAccessList.RollbackCurrentIndex();
                        args.Set(TxAction.Skip, result.ErrorDescription!);
                    }
                }

                return args.Action;

                [MethodImpl(MethodImplOptions.NoInlining)]
                void DebugSkipReason(Transaction currentTx, AddingTxEventArgs args)
                    => _logger.Debug($"Skipping transaction {currentTx.ToShortString()} because: {args.Reason}.");
            }
        }
    }
}
