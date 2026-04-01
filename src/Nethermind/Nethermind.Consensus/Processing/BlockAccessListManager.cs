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
using static Nethermind.Consensus.Processing.BlockProcessor;
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
    private BlockExecutionContext _blockExecutionContext;
    private TxProcessorWithWorldStateManager _txProcessorWithWorldStateManager;
    private const int GasValidationChunkSize = 8;
    private long _gasRemaining;

    public void Setup(Block block, bool parallel)
    {
        if (parallel)
        {
            _txProcessorWithWorldStateManager = new ParallelTxProcessorWithWorldStateManager(block, _blockExecutionContext, blocksConfig.ParallelExecution, blockHashProvider, specProvider, stateProvider, blobBaseFeeCalculator, logManager);
        }
        else
        {
            _txProcessorWithWorldStateManager = new SequentialTxProcessorWithWorldStateManager(block, _blockExecutionContext, blockHashProvider, specProvider, stateProvider, blobBaseFeeCalculator, logManager);
        }
    }

    public void SetGasUsed(long gasUsed)
        => _gasRemaining = gasUsed;

    public void SpendGas(long gas)
        => _gasRemaining -= gas;

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        => _blockExecutionContext = blockExecutionContext;

    public ITransactionProcessorAdapter GetTxProcessor(int? balIndex = null)
        => _txProcessorWithWorldStateManager.Get(balIndex).TxProcessorAdapter;

    public void NextTransaction()
    {
        _txProcessorWithWorldStateManager.Get().WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
        _txProcessorWithWorldStateManager.NextTransaction();
    }

    public void Rollback()
        => _txProcessorWithWorldStateManager.Rollback();

    public void IncrementalValidation(Block block, TaskCompletionSource<(long? BlockGasUsed, Exception? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler)
    {
        int len = block.Transactions.Length;
        _txProcessorWithWorldStateManager.GetPreExecution().WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
        ValidateBlockAccessList(block, 0);

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
                SpendGas(blockGasUsed.Value);

                bool validateStorageReads = j == chunkEnd - 1;
                _txProcessorWithWorldStateManager.Get(j + 1).WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
                ValidateBlockAccessList(block, (ushort)(j + 1), validateStorageReads);
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
        _txProcessorWithWorldStateManager.GetPostExecution().WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
        block.GeneratedBlockAccessList = GeneratedBlockAccessList;
        block.EncodedBlockAccessList = Rlp.Encode(GeneratedBlockAccessList).Bytes;
        block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
    }

    public void ValidateBlockAccessList(Block block, ushort index, bool validateStorageReads = true)
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

        if (validateStorageReads && _gasRemaining < (suggestedReads - generatedReads) * GasCostOf.ColdSLoad)
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

    public void ProcessWithdrawals(Block block, IReleaseSpec spec, bool parallel)
    {
        IWithdrawalProcessor withdrawalProcessor = new WithdrawalProcessor(_txProcessorWithWorldStateManager.GetPostExecution().WorldState, logManager);
        if (!parallel)
        {
            withdrawalProcessor = new BlockProductionWithdrawalProcessor(withdrawalProcessor);
        }
        withdrawalProcessor.ProcessWithdrawals(block, spec);
    }

    public void ProcessExecutionRequests(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)
    {
        TxProcessorWithWorldState postExecution = _txProcessorWithWorldStateManager.GetPostExecution();
        new ExecutionRequestsProcessor(postExecution.TxProcessor).ProcessExecutionRequests(block, postExecution.WorldState, txReceipts, spec);
    }

    public void LoadPreStateToSuggestedBlockAccessList(IReleaseSpec spec, Block suggested)
    {
        foreach (AccountChanges accountChanges in suggested.BlockAccessList.AccountChanges)
        {
            // check if changed before loading prestate
            accountChanges.CheckWasChanged();

            bool exists = stateProvider.TryGetAccount(accountChanges.Address, out AccountStruct account);
            accountChanges.ExistedBeforeBlock = exists;
            accountChanges.EmptyBeforeBlock = !account.HasStorage;

            accountChanges.AddBalanceChange(new(-1, account.Balance));
            accountChanges.AddNonceChange(new(-1, (ulong)account.Nonce));
            accountChanges.AddCodeChange(new(-1, stateProvider.GetCode(accountChanges.Address)));

            foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, slotChanges.Slot);
                slotChanges.AddStorageChange(new(-1, new(stateProvider.Get(storageCell), true)));
            }

            foreach (StorageRead storageRead in accountChanges.StorageReads)
            {
                SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(storageRead.Key);
                StorageCell storageCell = new(accountChanges.Address, storageRead.Key);
                slotChanges.AddStorageChange(new(-1, new(stateProvider.Get(storageCell), true)));
            }
        }
    }

    private static bool HasNoChanges(in ChangeAtIndex c)
        => c.BalanceChange is null &&
            c.NonceChange is null &&
            c.CodeChange is null &&
            !c.SlotChanges.GetEnumerator().MoveNext();


    private interface TxProcessorWithWorldStateManager
    {
        TxProcessorWithWorldState Get(int? balIndex = null);

        TxProcessorWithWorldState GetPreExecution()
            => Get(0);

        TxProcessorWithWorldState GetPostExecution()
            => Get(int.MaxValue);

        void NextTransaction();
        void Rollback();
    }

    private class ParallelTxProcessorWithWorldStateManager : TxProcessorWithWorldStateManager
    {
        private readonly TxProcessorWithWorldState[] _txProcessorsWithWorldStates;

        public ParallelTxProcessorWithWorldStateManager(
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

        public TxProcessorWithWorldState Get(int? balIndex)
            => _txProcessorsWithWorldStates[int.Min(balIndex ?? 0, _txProcessorsWithWorldStates.Length - 1)];

        public void NextTransaction() { }

        public void Rollback() { }
    }

    private class SequentialTxProcessorWithWorldStateManager(
        Block block,
        BlockExecutionContext blockExecutionContext,
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
        ILogManager logManager) : TxProcessorWithWorldStateManager
    {
        private readonly TxProcessorWithWorldState _txProcessorWithWorldState = new(block, blockExecutionContext, 0, false, blockHashProvider, specProvider, stateProvider, blobBaseFeeCalculator, logManager);

        public TxProcessorWithWorldState Get(int? _)
            => _txProcessorWithWorldState;

        public void NextTransaction()
        {
            _txProcessorWithWorldState.WorldState.Clear();
            _txProcessorWithWorldState.WorldState.IncrementIndex();
        }

        public void Rollback()
        {
            _txProcessorWithWorldState.WorldState.Clear();
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
            WorldState.SetIndex(balIndex);
            EthereumCodeInfoRepository codeInfoRepository = new(WorldState);
            TxProcessor = new(blobBaseFeeCalculator, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager);
            TxProcessorAdapter = new(TxProcessor);
            TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
        }
    }
}
