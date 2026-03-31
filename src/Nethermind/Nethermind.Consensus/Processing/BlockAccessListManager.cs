// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
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
    ILogManager logManager,
    IBlocksConfig blocksConfig)
{

    public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
    // private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;
    private BlockExecutionContext _blockExecutionContext;
    // private BlockAccessList[] _intermediateBlockAccessLists;
    // private ITransactionProcessorAdapter[] _transactionProcessorAdapters;
    // private ITransactionProcessor[] _transactionProcessors;
    // private TracedAccessWorldState[] _parallelWorldState;
    private TxProcessorWithWorldStateManager _txProcessorWithWorldStateManager;
    private const int GasValidationChunkSize = 8;
    private long _gasUsed;

    public void Setup(Block block, ProcessingOptions processingOptions)
    {
        if (processingOptions.ContainsFlag(ProcessingOptions.ProducingBlock))
        {
            _txProcessorWithWorldStateManager = new DynamicSizeTxProcessorWithWorldStateManager(block, _blockExecutionContext, blockHashProvider, specProvider, stateProvider, blobBaseFeeCalculator, logManager);
        }
        else
        {
            _txProcessorWithWorldStateManager = new FixedSizeTxProcessorWithWorldStateManager(block, _blockExecutionContext, blocksConfig.ParallelExecution, blockHashProvider, specProvider, stateProvider, blobBaseFeeCalculator, logManager);
        }
    }


    private void IncrementalValidation(Block block, TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers)
    {
        int len = block.Transactions.Length;
        long gasRemaining = _gasUsed;
        _txProcessorWithWorldStateManager.GetPreExecution().WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
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
                _txProcessorWithWorldStateManager.GetAtBalIndex(j + 1).WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
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
            _txProcessorWithWorldStateManager.GetPostExecution().WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
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
        TxProcessorWithWorldState preExecution = _txProcessorWithWorldStateManager.GetPreExecution();
        new BeaconBlockRootHandler(preExecution.TxProcessor, preExecution.WorldState).StoreBeaconRoot(block, spec, NullTxTracer.Instance);
    }

    public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec)
    {
        new BlockhashStore(_txProcessorWithWorldStateManager.GetPreExecution().WorldState).ApplyBlockhashStateChanges(header, spec);
    }

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        new WithdrawalProcessor(_txProcessorWithWorldStateManager.GetPostExecution().WorldState, logManager).ProcessWithdrawals(block, spec);
    }

    public void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)
    {
        TxProcessorWithWorldState postExecution = _txProcessorWithWorldStateManager.GetPostExecution();
        new ExecutionRequestsProcessor(postExecution.TxProcessor).ProcessExecutionRequests(block, postExecution.WorldState, txReceipts, spec);
    }

    private static bool HasNoChanges(in ChangeAtIndex c)
        => c.BalanceChange is null &&
            c.NonceChange is null &&
            c.CodeChange is null &&
            !c.SlotChanges.GetEnumerator().MoveNext();


    // private void MergeIntermediateBalsUpTo(ushort index, BlockAccessList[] intermediateBlockAccessLists)
    // {
    //     if (index == 0)
    //     {
    //         GeneratedBlockAccessList = intermediateBlockAccessLists[0];
    //     }
    //     else
    //     {
    //         GeneratedBlockAccessList.Merge(intermediateBlockAccessLists[index]);
    //     }
    // }

    private interface TxProcessorWithWorldStateManager
    {
        TxProcessorWithWorldState GetPreExecution();
        TxProcessorWithWorldState GetPostExecution();
        TxProcessorWithWorldState GetAtBalIndex(int txIndex);
        void AddTransaction();
        void Complete();
    }

    private class FixedSizeTxProcessorWithWorldStateManager : TxProcessorWithWorldStateManager
    {
        private readonly TxProcessorWithWorldState[] _txProcessorsWithWorldStates;

        public FixedSizeTxProcessorWithWorldStateManager(
            Block block,
            BlockExecutionContext blockExecutionContext,
            bool parallel,
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ILogManager logManager)
        {
            int len = block.Transactions.Length;
            _txProcessorsWithWorldStates = new TxProcessorWithWorldState[len + 2];
            for (int i = 0; i < len + 2; i++)
            {
                _txProcessorsWithWorldStates[i] = new(block, blockExecutionContext, i, parallel, blockHashProvider, specProvider, stateProvider, blobBaseFeeCalculator, logManager);
            }
        }

        public TxProcessorWithWorldState GetPostExecution()
            => _txProcessorsWithWorldStates[^1];

        public TxProcessorWithWorldState GetPreExecution()
            => _txProcessorsWithWorldStates[0];

        public TxProcessorWithWorldState GetAtBalIndex(int balIndex)
            => _txProcessorsWithWorldStates[balIndex];
        
        public void AddTransaction() {}

        public void Complete() {}
    }

    private class DynamicSizeTxProcessorWithWorldStateManager(
        Block block,
        BlockExecutionContext blockExecutionContext,
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ILogManager logManager) : TxProcessorWithWorldStateManager
    {
        private readonly List<TxProcessorWithWorldState> _txProcessorsWithWorldStates;
        private readonly TxProcessorWithWorldState _preExecTxProcessorsWithWorldStates = new(block, blockExecutionContext, 0, false, blockHashProvider, specProvider, stateProvider, blobBaseFeeCalculator, logManager);
        private readonly TxProcessorWithWorldState _postExecTxProcessorsWithWorldStates = new(block, blockExecutionContext, -1, false, blockHashProvider, specProvider, stateProvider, blobBaseFeeCalculator, logManager);

        public TxProcessorWithWorldState GetPostExecution()
            => _postExecTxProcessorsWithWorldStates;

        public TxProcessorWithWorldState GetPreExecution()
            => _preExecTxProcessorsWithWorldStates;

        public TxProcessorWithWorldState GetAtBalIndex(int balIndex)
            => _txProcessorsWithWorldStates[balIndex];

        public void AddTransaction()
            => _txProcessorsWithWorldStates.Add(new(block, blockExecutionContext, _txProcessorsWithWorldStates.Count + 1, false, blockHashProvider, specProvider, stateProvider, blobBaseFeeCalculator, logManager));

        public void Complete()
        {
            _postExecTxProcessorsWithWorldStates.WorldState.SetIndex(_txProcessorsWithWorldStates.Count + 1);
        }
    }

    private class TxProcessorWithWorldState
    {
        public TracedAccessWorldState WorldState;
        public TransactionProcessor<EthereumGasPolicy> TxProcessor;
        public ExecuteTransactionProcessorAdapter TxProcessorAdapter;

        public TxProcessorWithWorldState(
            Block block,
            BlockExecutionContext blockExecutionContext,
            int balIndex,
            bool parallel,
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ILogManager logManager)
        {

            VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
            IWorldState worldState = stateProvider;
            if (parallel)
            {
                worldState = new BlockAccessListBasedWorldState(stateProvider, balIndex, block, new(logManager));
            }
            WorldState = new TracedAccessWorldState(worldState);
            EthereumCodeInfoRepository codeInfoRepository = new(WorldState);
            TxProcessor = new(blobBaseFeeCalculator, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager);
            TxProcessorAdapter = new(TxProcessor);
            TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
        }
    }
}