// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using static Nethermind.State.BlockAccessListBasedWorldState;

namespace Nethermind.Consensus.Processing;

public class BlockAccessListManager(
    IWorldState stateProvider,
    ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
    ISpecProvider specProvider,
    IBlockhashProvider blockHashProvider,
    ILogManager logManager)
{

    public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
    // private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;
    private BlockExecutionContext _blockExecutionContext;
    private BlockAccessList[] _intermediateBlockAccessLists;
    private ITransactionProcessorAdapter[] _transactionProcessorAdapters;
    private ITransactionProcessor[] _transactionProcessors;
    private TracedAccessWorldState[] _parallelWorldState;
    private const int GasValidationChunkSize = 8;
    private long _gasUsed;

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

    private (ExecuteTransactionProcessorAdapter, TransactionProcessor<EthereumGasPolicy>, TracedAccessWorldState) CreateTransactionProcessor(Block block, int balIndex, bool isBuildingBlock)
    {
        VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
        BlockAccessListBasedWorldState balWorldState = new(stateProvider, balIndex, block, new(logManager));
        TracedAccessWorldState tracedWorldState = new(balWorldState);
        EthereumCodeInfoRepository codeInfoRepository = new(tracedWorldState);
        TransactionProcessor<EthereumGasPolicy> transactionProcessor = new(blobBaseFeeCalculator, specProvider, tracedWorldState, virtualMachine, codeInfoRepository, logManager);
        ExecuteTransactionProcessorAdapter transactionProcessorAdapter = new(transactionProcessor);
        transactionProcessorAdapter.SetBlockExecutionContext(_blockExecutionContext);

        return (transactionProcessorAdapter, transactionProcessor, tracedWorldState);
    }


    private void IncrementalValidation(Block block, TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers)
    {
        int len = block.Transactions.Length;
        long gasRemaining = _gasUsed;
        MergeIntermediateBalsUpTo(0, _intermediateBlockAccessLists);
        ValidateBlockAccessList(block, 0, gasRemaining);

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
                ValidateBlockAccessList(block, (ushort)(j + 1), gasRemaining, validateStorageReads);
            }

            if (totalGas > block.Header.GasLimit)
            {
                throw new InvalidBlockException(block, $"Block gas limit exceeded: cumulative gas {totalGas} > block gas limit {block.Header.GasLimit} after transaction index {chunkEnd - 1}.");
            }
        }
        _blockExecutionContext.Header.GasUsed = totalGas;
    }

    public static void ApplyStateChanges(BlockAccessList suggestedBlockAccessList, IWorldState stateProvider, IReleaseSpec spec, bool shouldComputeStateRoot)
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
        new WithdrawalProcessor(_parallelWorldState[^1], logManager).ProcessWithdrawals(block, spec);
    }

    public void ProcessExecutionRequests(Block block, IReleaseSpec spec)
    {
        new ExecutionRequestsProcessor(_transactionProcessors[^1]).ProcessExecutionRequests(block, _parallelWorldState[^1], _txReceipts, spec);
    }

    private static bool HasNoChanges(in ChangeAtIndex c)
        => c.BalanceChange is null &&
            c.NonceChange is null &&
            c.CodeChange is null &&
            !c.SlotChanges.GetEnumerator().MoveNext();


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
}