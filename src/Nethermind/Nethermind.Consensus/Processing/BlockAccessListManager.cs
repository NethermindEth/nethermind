// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Linq;
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
using Nethermind.Specs;
using static Nethermind.Consensus.Processing.BlockProcessor;
using static Nethermind.State.BlockAccessListBasedWorldState;
using System.Threading;

namespace Nethermind.Consensus.Processing;

public class BlockAccessListManager(
    IWorldState stateProvider,
    ISpecProvider specProvider,
    IBlockhashProvider blockHashProvider,
    ILogManager logManager,
    IBlocksConfig blocksConfig,
    IWithdrawalProcessorFactory withdrawalProcessorFactory)
    : IBlockAccessListManager
{
    public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
    public bool Enabled { get; private set; }
    public bool ParallelExecutionEnabled { get; private set; }
    private BlockExecutionContext? _blockExecutionContext;
    private TxProcessorWithWorldStateManager? _txProcessorWithWorldStateManager;
    private readonly TxProcessorWithWorldStateManager _parallelTxProcessorWithWorldStateManager = new(parallel: true, blockHashProvider, specProvider, stateProvider, logManager);
    private readonly TxProcessorWithWorldStateManager _sequentialTxProcessorWithWorldStateManager = new(parallel: false, blockHashProvider, specProvider, stateProvider, logManager);
    private const int GasValidationChunkSize = 8;
    private long? _gasRemaining;
    private bool _isBuilding;
    private bool _blockAccessListsEnabled;
    private Hash256 _lastLoadedBal = Hash256.Zero;

    private void Reset()
    {
        _txProcessorWithWorldStateManager = null;
        _blockExecutionContext = null;
        _gasRemaining = null;
        GeneratedBlockAccessList.Reset();
    }

    public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
    {
        _blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled;
        Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis;
        if (Enabled)
        {
            _isBuilding = options.ContainsFlag(ProcessingOptions.ProducingBlock);
            // Parallel execution requires the BAL body to be present on the block.
            // Blocks from p2p/RLP fixtures only have the header hash, not the decoded BAL body.
            ParallelExecutionEnabled = blocksConfig.ParallelExecution && !_isBuilding && suggestedBlock.BlockAccessList is not null;
            Reset();
            _gasRemaining = suggestedBlock.GasUsed;

            // check last loaded bal to avoid loading prestate again
            if (ParallelExecutionEnabled && suggestedBlock.Hash != _lastLoadedBal)
            {
                _lastLoadedBal = suggestedBlock.Hash;
                LoadPreStateToSuggestedBlockAccessList(suggestedBlock.BlockAccessList);
            }
        }
    }

    public void Setup(Block block)
    {
        if (Enabled)
        {
            _txProcessorWithWorldStateManager = ParallelExecutionEnabled ? _parallelTxProcessorWithWorldStateManager : _sequentialTxProcessorWithWorldStateManager;
            _txProcessorWithWorldStateManager.Setup(block, _blockExecutionContext.Value);
        }
    }

    public void SpendGas(long gas)
        => _gasRemaining -= gas;

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        => _blockExecutionContext = blockExecutionContext;

    public ITransactionProcessorAdapter GetTxProcessor(int? balIndex = null)
        => _txProcessorWithWorldStateManager.Get(balIndex).TxProcessorAdapter;

    public void NextTransaction()
    {
        if (Enabled)
        {
            _txProcessorWithWorldStateManager.Get().WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
            _txProcessorWithWorldStateManager.NextTransaction();
        }
    }

    public void Rollback()
    {
        if (Enabled)
        {
            _txProcessorWithWorldStateManager.Rollback();
        }
    }

    public void IncrementalValidation(Block block, TaskCompletionSource<(long? BlockGasUsed, long BlockStateGasUsed, Exception? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token)
    {
        int len = block.Transactions.Length;
        _txProcessorWithWorldStateManager.GetPreExecution().WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
        ValidateBlockAccessList(block, 0);

        long totalRegularGas = 0;
        long totalStateGas = 0;
        for (int chunkStart = 0; chunkStart < len; chunkStart += GasValidationChunkSize)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            int chunkEnd = Math.Min(chunkStart + GasValidationChunkSize, len);
            for (int j = chunkStart; j < chunkEnd; j++)
            {
                (long? blockGasUsed, long blockStateGasUsed, Exception? ex) = gasResults[j].Task.GetAwaiter().GetResult();
                totalRegularGas += blockGasUsed.Value;
                totalStateGas += blockStateGasUsed;
                SpendGas(blockGasUsed.Value);

                CheckGasUsed(j, block, totalRegularGas, totalStateGas);

                if (ex is not null)
                    ExceptionDispatchInfo.Capture(ex).Throw();

                transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(j, block.Transactions[j], block.Header, receiptsTracers[j].TxReceipts[0]));

                bool validateStorageReads = j == chunkEnd - 1;
                _txProcessorWithWorldStateManager.Get(j + 1).WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
                ValidateBlockAccessList(block, (ushort)(j + 1), validateStorageReads);
            }
        }

        // EIP-8037: 2D gas accounting — block gasUsed = max(sum_regular, sum_state)
        _blockExecutionContext.Value.Header.GasUsed = Math.Max(totalRegularGas, totalStateGas);

        static void CheckGasUsed(int index, Block block, long totalRegularGas, long totalStateGas)
        {
            // EIP-8037: block gasUsed = max(sum_regular, sum_state)
            long effectiveGas = Math.Max(totalRegularGas, totalStateGas);
            if (effectiveGas > block.Header.GasLimit)
            {
                throw new InvalidBlockException(block, $"Block gas limit exceeded: cumulative gas {effectiveGas} > block gas limit {block.Header.GasLimit} after transaction index {index}.");
            }
        }
    }

    public static void ApplyStateChanges(BlockAccessList suggestedBlockAccessList, IWorldState stateProvider, IReleaseSpec spec, bool shouldComputeStateRoot)
    {
        foreach (AccountChanges accountChanges in suggestedBlockAccessList.AccountChanges)
        {
            if (accountChanges.BalanceChanges.Count > 0 && accountChanges.BalanceChanges[accountChanges.BalanceChanges.Count - 1].BlockAccessIndex != -1)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                UInt256 oldBalance = accountChanges.GetBalance(0) ?? UInt256.Zero;
                UInt256 newBalance = accountChanges.BalanceChanges[accountChanges.BalanceChanges.Count - 1].PostBalance;
                if (newBalance > oldBalance)
                {
                    stateProvider.AddToBalance(accountChanges.Address, newBalance - oldBalance, spec);
                }
                else
                {
                    stateProvider.SubtractFromBalance(accountChanges.Address, oldBalance - newBalance, spec);
                }
            }

            if (accountChanges.NonceChanges.Count > 0 && accountChanges.NonceChanges[accountChanges.NonceChanges.Count - 1].BlockAccessIndex != -1)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                stateProvider.SetNonce(accountChanges.Address, accountChanges.NonceChanges[accountChanges.NonceChanges.Count - 1].NewNonce);
            }

            if (accountChanges.CodeChanges.Count > 0 && accountChanges.CodeChanges[accountChanges.CodeChanges.Count - 1].BlockAccessIndex != -1)
            {
                stateProvider.InsertCode(accountChanges.Address, accountChanges.CodeChanges[accountChanges.CodeChanges.Count - 1].NewCode, spec);
            }

            foreach (SlotChanges slotChange in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, slotChange.Slot);
                // could be empty since prestate loaded
                int slotCount = slotChange.Changes.Count;
                if (slotCount > 0 && slotChange.Changes.Keys[slotCount - 1] != -1)
                {
                    stateProvider.Set(storageCell, [.. slotChange.Changes.Values[slotCount - 1].NewValue.ToBigEndian().WithoutLeadingZeros()]);
                }
            }
        }
        stateProvider.Commit(spec);
        if (shouldComputeStateRoot)
        {
            stateProvider.RecalculateStateRoot();
        }
    }

    public void ApplyAuRaPreprocessingChanges(IReleaseSpec spec, Address withdrawalContractAddress)
    {
        if (!Enabled)
        {
            return;
        }

        stateProvider.CreateAccount(Address.SystemUser, UInt256.Zero, UInt256.Zero);
        stateProvider.CreateAccount(withdrawalContractAddress, UInt256.Zero, UInt256.Zero);
        stateProvider.Commit(spec.ForSystemTransaction(true, false), commitRoots: false);
    }

    public void SetBlockAccessList(Block block)
    {
        if (!_blockAccessListsEnabled)
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

    // todo: optimize early validation
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
                if (IsSystemAccountRead(generatedHead.Value, index) || HasOptionalStorageReads(generatedHead.Value))
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
                    generatedHead.Value.CodeChange.HasValue != suggestedHead.Value.CodeChange.HasValue ||
                    generatedHead.Value.CodeChange is not null && !generatedHead.Value.CodeChange.Value.Equals(suggestedHead.Value.CodeChange.Value) ||
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
                if (IsSystemAccountRead(generatedHead.Value, index) || HasOptionalStorageReads(generatedHead.Value))
                {
                    AdvanceGenerated();
                    continue;
                }
                throw new InvalidBlockLevelAccessListException(block.Header, $"Suggested block-level access list missing account changes for {generatedHead.Value.Address} at index {index}.");
            }

            AdvanceGenerated();
            AdvanceSuggested();
        }

        int surplusSuggestedReads = suggestedReads - generatedReads;
        if (validateStorageReads && surplusSuggestedReads > 0 && _gasRemaining < surplusSuggestedReads * Eip7928Constants.ItemCost)
        {
            throw new InvalidBlockLevelAccessListException(block.Header, "Suggested block-level access list contained invalid storage reads.");
        }
    }

    public void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        TxProcessorWithWorldState preExecution = _txProcessorWithWorldStateManager.GetPreExecution();
        new BeaconBlockRootHandler(preExecution.TxProcessor, preExecution.WorldState).StoreBeaconRoot(block, spec, NullTxTracer.Instance);
    }

    public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec) => new BlockhashStore(_txProcessorWithWorldStateManager.GetPreExecution().WorldState).ApplyBlockhashStateChanges(header, spec);

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        TxProcessorWithWorldState postExecution = _txProcessorWithWorldStateManager.GetPostExecution();
        IWithdrawalProcessor withdrawalProcessor = withdrawalProcessorFactory.Create(postExecution.WorldState, postExecution.TxProcessor);
        if (_isBuilding)
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

    private void LoadPreStateToSuggestedBlockAccessList(BlockAccessList bal)
    {
        foreach (AccountChanges accountChanges in bal.AccountChanges)
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

            foreach (UInt256 storageRead in accountChanges.StorageReads)
            {
                SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(storageRead);
                StorageCell storageCell = new(accountChanges.Address, storageRead);
                slotChanges.AddStorageChange(new(-1, new(stateProvider.Get(storageCell), true)));
            }
        }
    }

    private static bool HasNoChanges(in ChangeAtIndex c)
        => c.BalanceChange is null &&
            c.NonceChange is null &&
            c.CodeChange is null &&
            !c.HasSlotChanges;

    private static bool HasOptionalStorageReads(in ChangeAtIndex c)
        => HasNoChanges(c) && c.Reads > 0;

    private static bool IsSystemAccountRead(in ChangeAtIndex c, ushort index)
        => index == 0 && c.Address == Address.SystemUser && HasNoChanges(c) && c.Reads == 0;

    private class TxProcessorWithWorldStateManager(
        bool parallel,
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ILogManager logManager)
    {
        private TxProcessorWithWorldState[] _processors = parallel
            ? []
            : [new(0, false, blockHashProvider, specProvider, stateProvider, logManager)];

        public void Setup(Block block, BlockExecutionContext blockExecutionContext)
        {
            if (parallel)
            {
                int len = block.Transactions.Length + 2;
                _processors = new TxProcessorWithWorldState[len];
                for (int i = 0; i < len; i++)
                {
                    // todo: could be a lot of allocations here
                    // will optimize to allocate ~16 worldstates upfront, and reuse them as they are ready
                    _processors[i] = new(i, true, blockHashProvider, specProvider, stateProvider, logManager);
                    _processors[i].Setup(block, blockExecutionContext);
                }
            }
            else
            {
                _processors[0].Setup(block, blockExecutionContext);
            }
        }

        public TxProcessorWithWorldState Get(int? balIndex = null)
            => _processors[parallel ? int.Min(balIndex ?? 0, _processors.Length - 1) : 0];

        public TxProcessorWithWorldState GetPreExecution() => Get(0);

        public TxProcessorWithWorldState GetPostExecution() => Get(int.MaxValue);

        public void NextTransaction()
        {
            if (!parallel)
            {
                TxProcessorWithWorldState p = _processors[0];
                p.WorldState.Clear();
                p.WorldState.IncrementIndex();
            }
        }

        public void Rollback()
        {
            if (!parallel)
            {
                _processors[0].WorldState.Clear();
            }
        }
    }

    private class TxProcessorWithWorldState
    {
        public readonly TracedAccessWorldState WorldState;
        public readonly TransactionProcessor<EthereumGasPolicy> TxProcessor;
        public readonly ExecuteTransactionProcessorAdapter TxProcessorAdapter;
        private readonly BlockAccessListBasedWorldState? _balWorldState;
        private readonly int _balIndex;

        public TxProcessorWithWorldState(
            int balIndex,
            bool parallel,
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager)
        {

            VirtualMachine virtualMachine = new(blockHashProvider, specProvider, logManager);
            IWorldState worldState = stateProvider;
            if (parallel)
            {
                _balWorldState = new BlockAccessListBasedWorldState(stateProvider, balIndex, logManager);
                worldState = _balWorldState;
            }
            WorldState = new TracedAccessWorldState(worldState, parallel);
            WorldState.SetIndex(balIndex);
            EthereumCodeInfoRepository codeInfoRepository = new(WorldState);
            TxProcessor = new(BlobBaseFeeCalculator.Instance, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager, parallel);
            TxProcessorAdapter = new(TxProcessor);
            _balIndex = balIndex;
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext)
        {
            WorldState.Clear();
            WorldState.SetIndex(_balIndex);
            TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
            _balWorldState?.Setup(block);
        }
    }
}
