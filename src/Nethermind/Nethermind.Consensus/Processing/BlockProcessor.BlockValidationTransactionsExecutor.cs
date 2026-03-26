// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using static Nethermind.State.ParallelWorldState;
using Metrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockValidationTransactionsExecutor(
            IWorldState stateProvider,
            ITransactionProcessorAdapter transactionProcessor,
            ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ISpecProvider specProvider,
            IBlockhashProvider blockHashProvider,
            ICodeInfoRepository codeInfoRepository,
            ILogManager logManager,
            IBlocksConfig blocksConfig,
            BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler = null)
            : IBlockProcessor.IBlockTransactionsExecutor
        {
            public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
            // private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;
            private readonly ITransactionProcessorAdapter _transactionProcessor = transactionProcessor;
            private BlockExecutionContext _blockExecutionContext;
            private BlockAccessList[] _intermediateBlockAccessLists;
            private const int GasValidationChunkSize = 8;

            public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
            {
                _transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
                _blockExecutionContext = blockExecutionContext;
            }

            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
            {
                Metrics.ResetBlockStats();

                return blocksConfig.ParallelExecution && !block.IsGenesis ?
                    ProcessTransactionsParallel(block, processingOptions, token) :
                    ProcessTransactionsSequential(block, processingOptions, receiptsTracer, token);
            }

            private TxReceipt[] ProcessTransactionsSequential(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token)
            {
                // todo: add parallel to _transactionprocessor
                VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
                ParallelWorldState parallelWorldState = new(stateProvider, specProvider, blocksConfig, -1, block, new(), GeneratedBlockAccessList, new(logManager), processingOptions.ContainsFlag(ProcessingOptions.ProducingBlock));
                TransactionProcessor<EthereumGasPolicy> transactionProcessor = new(blobBaseFeeCalculator, specProvider, parallelWorldState, virtualMachine, codeInfoRepository, logManager);
                ExecuteTransactionProcessorAdapter transactionProcessorAdapter = new(transactionProcessor);
                transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);

                long? gasRemaining = block.GasUsed;
                if (gasRemaining is not null)
                {
                    ValidateBlockAccessList(block, 0, gasRemaining.Value);
                }

                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    GeneratedBlockAccessList.IncrementBlockAccessIndex();
                    Transaction currentTx = block.Transactions[i];
                    ProcessTransaction(transactionProcessorAdapter, block, currentTx, i, receiptsTracer, processingOptions);

                    if (gasRemaining is not null)
                    {
                        gasRemaining -= currentTx.BlockGasUsed;
                        ValidateBlockAccessList(block, (ushort)(i + 1), gasRemaining!.Value);
                    }
                }
                GeneratedBlockAccessList.IncrementBlockAccessIndex();

                return [.. receiptsTracer.TxReceipts];
            }

            private TxReceipt[] ProcessTransactionsParallel(Block block, ProcessingOptions processingOptions, CancellationToken token)
            {
                int len = block.Transactions.Length;
                ITransactionProcessorAdapter[] transactionProcessors = new ITransactionProcessorAdapter[len];
                _intermediateBlockAccessLists = new BlockAccessList[len + 2];
                TransientStorageProvider[] transientStorageProviders = new TransientStorageProvider[len + 2];
                BlockReceiptsTracer[] receiptsTracers = new BlockReceiptsTracer[len];
                TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[] gasResults = new TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[len];

                for (int i = 0; i < len + 2; i++)
                {
                    BlockAccessList bal = new()
                    {
                        Index = i
                    };
                    _intermediateBlockAccessLists[i] = bal;
                    transientStorageProviders[i] = new(logManager);
                }

                for (int i = 0; i < len; i++)
                {
                    // todo: look into reusing / reducing allocation
                    VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
                    ParallelWorldState parallelWorldState = new(stateProvider, specProvider, blocksConfig, i + 1, block, _intermediateBlockAccessLists[i + 1], GeneratedBlockAccessList, transientStorageProviders[i + 1], processingOptions.ContainsFlag(ProcessingOptions.ProducingBlock));
                    TransactionProcessor<EthereumGasPolicy> transactionProcessor = new(blobBaseFeeCalculator, specProvider, parallelWorldState, virtualMachine, codeInfoRepository, logManager);
                    ExecuteTransactionProcessorAdapter transactionProcessorAdapter = new(transactionProcessor);
                    transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);
                    transactionProcessors[i] = transactionProcessorAdapter;

                    BlockReceiptsTracer tracer = new();
                    tracer.StartNewBlockTrace(block);
                    receiptsTracers[i] = tracer;
                    gasResults[i] = new TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>();
                }

                Task incrementalValidationTask = Task.Run(() => IncrementalValidation(block, gasResults, receiptsTracers), token);

                ParallelUnbalancedWork.For(
                    0,
                    len + 1,
                    ParallelUnbalancedWork.DefaultOptions,
                    (block, processingOptions, stateProvider, transactionProcessors, receiptsTracers, gasResults, specProvider, txs: block.Transactions),
                    static (i, state) =>
                    {
                        if (i == 0)
                        {
                            // (state.stateProvider as IBlockAccessListBuilder)?.ApplyStateChanges(state.specProvider.GetSpec(state.block.Header), !state.block.Header.IsGenesis || !state.specProvider.GenesisStateUnavailable);
                            ApplyStateChanges(state.block.BlockAccessList, state.stateProvider, state.specProvider.GetSpec(state.block.Header), !state.block.Header.IsGenesis || !state.specProvider.GenesisStateUnavailable);
                            return state;
                        }

                        int txIndex = i - 1;
                        try
                        {
                            Transaction tx = state.txs[txIndex];
                            ProcessTransactionParallel(
                                state.transactionProcessors[txIndex],
                                state.stateProvider,
                                state.block,
                                tx,
                                txIndex,
                                state.receiptsTracers[txIndex],
                                state.processingOptions);
                            state.gasResults[txIndex].SetResult((tx.BlockGasUsed, null));
                        }
                        catch (Exception ex)
                        {
                            state.gasResults[txIndex].SetResult((null, ex));
                        }

                        return state;
                    });

                incrementalValidationTask.GetAwaiter().GetResult();
                return CombineReceipts(receiptsTracers, len, block);
            }

            private void IncrementalValidation(Block block, TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers)
            {
                int len = block.Transactions.Length;
                long gasRemaining = block.GasUsed;
                // ValidateBlockAccessList(block, 0, gasRemaining);

                long totalGas = 0;
                for (int chunkStart = 0; chunkStart < len; chunkStart += GasValidationChunkSize)
                {
                    int chunkEnd = Math.Min(chunkStart + GasValidationChunkSize, len);
                    for (int j = chunkStart; j < chunkEnd; j++)
                    {
                        (long? blockGasUsed, Exception? ex) = gasResults[j].Task.GetAwaiter().GetResult();
                        if (ex is not null)
                            ExceptionDispatchInfo.Capture(ex).Throw();

                        transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(j, block.Transactions[j], block.Header, receiptsTracers[j].TxReceipts[0]));

                        totalGas += blockGasUsed.Value;
                        gasRemaining -= blockGasUsed.Value;

                        bool validateStorageReads = j == chunkEnd - 1;
                        MergeIntermediateBalsUpTo((ushort)(j + 1), _intermediateBlockAccessLists);
                        // ValidateBlockAccessList(block, (ushort)(j + 1), gasRemaining, validateStorageReads);
                    }

                    if (totalGas > block.Header.GasLimit)
                    {
                        throw new InvalidBlockException(block, $"Block gas limit exceeded: cumulative gas {totalGas} > block gas limit {block.Header.GasLimit} after transaction index {chunkEnd - 1}.");
                    }
                }
                _blockExecutionContext.Header.GasUsed = totalGas;
            }

            private static TxReceipt[] CombineReceipts(BlockReceiptsTracer[] receiptsTracers, int len, Block block)
            {
                TxReceipt[] result = new TxReceipt[len];
                long cumulativeGas = 0;
                Bloom blockBloom = new();
                for (int i = 0; i < len; i++)
                {
                    result[i] = receiptsTracers[i].TxReceipts[0];
                    result[i].Index = i;
                    cumulativeGas += result[i].GasUsed;
                    result[i].GasUsedTotal = cumulativeGas;
                    result[i].CalculateBloom();
                    blockBloom.Accumulate(result[i].Bloom!);
                }

                block.Header.Bloom = blockBloom;

                return result;
            }

            protected virtual void ProcessTransaction(ITransactionProcessorAdapter transactionProcessor, Block block, Transaction currentTx, int index, BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions)
            {
                currentTx.BlockAccessIndex = index + 1;
                TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
                if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
                transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));
            }

            private static void ProcessTransactionParallel(
                ITransactionProcessorAdapter transactionProcessor,
                IWorldState stateProvider,
                Block block,
                Transaction currentTx,
                int index,
                BlockReceiptsTracer receiptsTracer,
                ProcessingOptions processingOptions)
            {
                currentTx.BlockAccessIndex = index + 1;
                TransactionResult result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, stateProvider);
                if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowInvalidTransactionException(TransactionResult result, BlockHeader header, Transaction currentTx, int index)
            {
                throw new InvalidTransactionException(header, $"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}", result);
            }

            /// <summary>
            /// Used by <see cref="FilterManager"/> through <see cref="IMainProcessingContext"/>
            /// </summary>
            public interface ITransactionProcessedEventHandler
            {
                void OnTransactionProcessed(TxProcessedEventArgs txProcessedEventArgs);
            }

            private static void ApplyStateChanges(BlockAccessList suggestedBlockAccessList, IWorldState stateProvider, IReleaseSpec spec, bool shouldComputeStateRoot)
            {
                foreach (AccountChanges accountChanges in suggestedBlockAccessList.AccountChanges)
                {
                    if (accountChanges.BalanceChanges.Count > 0 && accountChanges.BalanceChanges.Last().BlockAccessIndex != -1)
                    {
                        stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                        UInt256 oldBalance = accountChanges.GetBalance(0);
                        UInt256 newBalance = accountChanges.BalanceChanges.Last().PostBalance;
                        if (newBalance > oldBalance)
                        {
                            stateProvider.AddToBalance(accountChanges.Address, newBalance - oldBalance, spec);
                        }
                        else
                        {
                            stateProvider.SubtractFromBalance(accountChanges.Address, oldBalance - newBalance, spec);
                        }
                    }

                    if (accountChanges.NonceChanges.Count > 0 && accountChanges.NonceChanges.Last().BlockAccessIndex != -1)
                    {
                        stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                        stateProvider.SetNonce(accountChanges.Address, accountChanges.NonceChanges.Last().NewNonce);
                    }

                    if (accountChanges.CodeChanges.Count > 0 && accountChanges.CodeChanges.Last().BlockAccessIndex != -1)
                    {
                        stateProvider.InsertCode(accountChanges.Address, accountChanges.CodeChanges.Last().NewCode, spec);
                    }

                    foreach (SlotChanges slotChange in accountChanges.StorageChanges)
                    {
                        StorageCell storageCell = new(accountChanges.Address, slotChange.Slot);
                        // could be empty since prestate loaded
                        if (slotChange.Changes.Count > 0 && slotChange.Changes.Last().Key != -1)
                        {
                            stateProvider.Set(storageCell, [.. slotChange.Changes.Last().Value.NewValue.ToBigEndian().WithoutLeadingZeros()]);
                        }
                    }
                }
                stateProvider.Commit(spec);
                if (shouldComputeStateRoot)
                {
                    stateProvider.RecalculateStateRoot();
                }
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

            public void ValidateBlockAccessList(Block block, ushort index, long gasRemaining, bool validateStorageReads = true)
            {
                if (block.BlockAccessList is null)
                {
                    return;
                }

                IEnumerator<ChangeAtIndex> generatedChanges = GeneratedBlockAccessList.GetChangesAtIndex(index).GetEnumerator();
                IEnumerator<ChangeAtIndex> suggestedChanges = block.BlockAccessList.GetChangesAtIndex(index).GetEnumerator();

                ChangeAtIndex? generatedHead;
                ChangeAtIndex? suggestedHead;

                int generatedReads = 0;
                int suggestedReads = 0;

                void AdvanceGenerated()
                {
                    generatedHead = generatedChanges.MoveNext() ? generatedChanges.Current : null;
                    if (generatedHead is not null) generatedReads += generatedHead.Value.Reads;
                }

                void AdvanceSuggested()
                {
                    suggestedHead = suggestedChanges.MoveNext() ? suggestedChanges.Current : null;
                    if (suggestedHead is not null) suggestedReads += suggestedHead.Value.Reads;
                }

                AdvanceGenerated();
                AdvanceSuggested();

                while (generatedHead is not null || suggestedHead is not null)
                {
                    if (suggestedHead is null)
                    {
                        if (HasNoChanges(generatedHead.Value))
                        {
                            AdvanceGenerated();
                            continue;
                        }
                        throw new InvalidBlockLevelAccessListException(block.Header, $"Suggested block-level access list missing account changes for {generatedHead.Value.Address} at index {index}.");
                    }
                    else if (generatedHead is null)
                    {
                        if (HasNoChanges(suggestedHead.Value))
                        {
                            AdvanceSuggested();
                            continue;
                        }
                        throw new InvalidBlockLevelAccessListException(block.Header, $"Suggested block-level access list contained surplus changes for {suggestedHead.Value.Address} at index {index}.");
                    }

                    int cmp = generatedHead.Value.Address.CompareTo(suggestedHead.Value.Address);

                    if (cmp == 0)
                    {
                        if (generatedHead.Value.BalanceChange != suggestedHead.Value.BalanceChange ||
                            generatedHead.Value.NonceChange != suggestedHead.Value.NonceChange ||
                            generatedHead.Value.CodeChange != suggestedHead.Value.CodeChange ||
                            !Enumerable.SequenceEqual(generatedHead.Value.SlotChanges, suggestedHead.Value.SlotChanges))
                        {
                            throw new InvalidBlockLevelAccessListException(block.Header, $"Suggested block-level access list contained incorrect changes for {suggestedHead.Value.Address} at index {index}.");
                        }
                    }
                    else if (cmp > 0)
                    {
                        if (HasNoChanges(suggestedHead.Value))
                        {
                            AdvanceSuggested();
                            continue;
                        }
                        throw new InvalidBlockLevelAccessListException(block.Header, $"Suggested block-level access list contained surplus changes for {suggestedHead.Value.Address} at index {index}.");
                    }
                    else
                    {
                        if (HasNoChanges(generatedHead.Value))
                        {
                            AdvanceGenerated();
                            continue;
                        }
                        throw new InvalidBlockLevelAccessListException(block.Header, $"Suggested block-level access list missing account changes for {generatedHead.Value.Address} at index {index}.");
                    }

                    AdvanceGenerated();
                    AdvanceSuggested();
                }

                if (validateStorageReads && gasRemaining < (suggestedReads - generatedReads) * GasCostOf.ColdSLoad)
                {
                    throw new InvalidBlockLevelAccessListException(block.Header, "Suggested block-level access list contained invalid storage reads.");
                }
            }

            private static bool HasNoChanges(in ChangeAtIndex c)
                => c.BalanceChange is null &&
                    c.NonceChange is null &&
                    c.CodeChange is null &&
                    !c.SlotChanges.GetEnumerator().MoveNext();


            public void MergeIntermediateBalsUpTo(ushort index, BlockAccessList[] intermediateBlockAccessLists)
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
        }
    }
}
