// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    public class ParallelExecutionException(InvalidBlockException innerException)
        : InvalidTransactionException(
            innerException.InvalidBlock,
            innerException.Message,
            (innerException as InvalidTransactionException)?.Reason
                ?? TransactionResult.WithDetail(TransactionResult.ErrorType.MalformedTransaction, $"Parallel execution failure: {innerException.Message}"),
            innerException);
    public BlockAccessList GeneratedBlockAccessList { get; set; } = new();
    public bool Enabled { get; private set; }
    public bool ParallelExecutionEnabled { get; private set; }
    private BlockExecutionContext? _blockExecutionContext;
    private ITxProcessorWithWorldStateManager? _txProcessorWithWorldStateManager;
    private readonly ParallelTxProcessorWithWorldStateManager _parallelTxProcessorWithWorldStateManager = new(blockHashProvider, specProvider, stateProvider, logManager);
    private readonly SequentialTxProcessorWithWorldStateManager _sequentialTxProcessorWithWorldStateManager = new(blockHashProvider, specProvider, stateProvider, logManager);
    private const int GasValidationChunkSize = 8;
    private long? _gasRemaining;
    private bool _isBuilding;
    private bool _blockAccessListsEnabled;
    // Cache key guarding LoadPreStateToSuggestedBlockAccessList against double-mutation of the
    // suggested block's BAL: that method appends -1 (pre-state) entries in place, so calling it
    // twice for the same Block instance corrupts the BAL. PrepareForProcessing can be invoked
    // more than once per block within the same DI scope (the manager is scoped to the main
    // processing context, not per block) — e.g. on retry — so we skip the load when the hash
    // matches the most recently loaded one.
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
        _isBuilding = options.ContainsFlag(ProcessingOptions.ProducingBlock);

        // Parallel execution requires the BAL body to be present on the block.
        // Blocks from p2p/RLP fixtures only have the header hash, not the decoded BAL body.
        ParallelExecutionEnabled = Enabled && blocksConfig.ParallelExecution && !_isBuilding && suggestedBlock.BlockAccessList is not null;

        if (Enabled)
        {
            Reset();
            _gasRemaining = suggestedBlock.GasUsed;

            // See _lastLoadedBal field comment — skip when the same block is re-prepared.
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
            CheckInitialized();
            _txProcessorWithWorldStateManager.Setup(block, _blockExecutionContext.Value);
        }
    }

    public void SpendGas(long gas)
    {
        CheckInitialized();
        _gasRemaining -= gas;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        => _blockExecutionContext = blockExecutionContext;

    public ITransactionProcessorAdapter GetTxProcessor(int? balIndex = null)
    {
        CheckInitialized();
        return _txProcessorWithWorldStateManager.Get(balIndex).TxProcessorAdapter;
    }

    public void NextTransaction()
    {
        if (Enabled)
        {
            CheckInitialized();
            _txProcessorWithWorldStateManager.Get().WorldState.MergeGeneratingBal(GeneratedBlockAccessList);
            _txProcessorWithWorldStateManager.NextTransaction();
        }
    }

    public void Rollback()
    {
        if (Enabled)
        {
            CheckInitialized();
            _txProcessorWithWorldStateManager.Rollback();
        }
    }

    public void IncrementalValidation(Block block, TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, InvalidBlockException? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token)
    {
        CheckInitialized();

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
                (long blockGasUsed, long blockStateGasUsed, InvalidBlockException? ex) = gasResults[j].Task.GetAwaiter().GetResult();
                totalRegularGas += blockGasUsed;
                totalStateGas += blockStateGasUsed;
                SpendGas(blockGasUsed);

                CheckGasUsed(j, block, totalRegularGas, totalStateGas);

                if (ex is not null)
                    throw new ParallelExecutionException(ex);

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
            if (accountChanges.BalanceChanges.Count > 0 && accountChanges.BalanceChanges[^1].Index != -1)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                UInt256 oldBalance = accountChanges.GetBalance(0) ?? UInt256.Zero;
                UInt256 newBalance = accountChanges.BalanceChanges[^1].Value;
                if (newBalance > oldBalance)
                {
                    stateProvider.AddToBalance(accountChanges.Address, newBalance - oldBalance, spec);
                }
                else
                {
                    stateProvider.SubtractFromBalance(accountChanges.Address, oldBalance - newBalance, spec);
                }
            }

            if (accountChanges.NonceChanges.Count > 0 && accountChanges.NonceChanges[^1].Index != -1)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                stateProvider.SetNonce(accountChanges.Address, accountChanges.NonceChanges[^1].Value);
            }

            if (accountChanges.CodeChanges.Count > 0 && accountChanges.CodeChanges[^1].Index != -1)
            {
                stateProvider.InsertCode(accountChanges.Address, accountChanges.CodeChanges[^1].Code, spec);
            }

            foreach (SlotChanges slotChange in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, slotChange.Key);
                // could be empty since prestate loaded
                int slotCount = slotChange.Changes.Count;
                if (slotCount > 0 && slotChange.Changes.Keys[slotCount - 1] != -1)
                {
                    stateProvider.Set(storageCell, [.. slotChange.Changes.Values[slotCount - 1].Value.ToBigEndian().WithoutLeadingZeros()]);
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
            CheckInitialized();

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

        CheckInitialized();

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
        CheckInitialized();

        TxProcessorWithWorldState preExecution = _txProcessorWithWorldStateManager.GetPreExecution();
        new BeaconBlockRootHandler(preExecution.TxProcessor, preExecution.WorldState).StoreBeaconRoot(block, spec, NullTxTracer.Instance);
    }

    public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec)
    {
        CheckInitialized();
        new BlockhashStore(_txProcessorWithWorldStateManager.GetPreExecution().WorldState).ApplyBlockhashStateChanges(header, spec);
    }

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        CheckInitialized();

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
        CheckInitialized();

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
                StorageCell storageCell = new(accountChanges.Address, slotChanges.Key);
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

    private void CheckInitialized()
    {
        if (_txProcessorWithWorldStateManager is null)
            throw new InvalidOperationException($"{nameof(_txProcessorWithWorldStateManager)} was not initialized.");

        if (_gasRemaining is null)
            throw new InvalidOperationException($"{nameof(_gasRemaining)} was not initialized.");

        if (_blockExecutionContext is null)
            throw new InvalidOperationException($"{nameof(_blockExecutionContext)} was not initialized.");
    }

    private interface ITxProcessorWithWorldStateManager
    {
        void Setup(Block block, BlockExecutionContext blockExecutionContext);
        TxProcessorWithWorldState Get(int? balIndex = null);
        TxProcessorWithWorldState GetPreExecution() => Get(0);
        TxProcessorWithWorldState GetPostExecution() => Get(int.MaxValue);
        void NextTransaction();
        void Rollback();
    }

    private class ParallelTxProcessorWithWorldStateManager(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ILogManager logManager) : ITxProcessorWithWorldStateManager
    {
        private TxProcessorWithWorldState[] _txProcessorsWithWorldStates;
        private int _len;

        public void Setup(Block block, BlockExecutionContext blockExecutionContext)
        {
            _len = block.Transactions.Length + 2;
            _txProcessorsWithWorldStates = new TxProcessorWithWorldState[_len];
            for (int i = 0; i < _len; i++)
            {
                // todo: could be a lot of allocations here
                // will optimize to allocate ~16 worldstates upfront, and reuse them as they are ready
                _txProcessorsWithWorldStates[i] = new(i, true, blockHashProvider, specProvider, stateProvider, logManager);
                _txProcessorsWithWorldStates[i].Setup(block, blockExecutionContext);
            }
        }

        public TxProcessorWithWorldState Get(int? balIndex)
            => _txProcessorsWithWorldStates[int.Min(balIndex ?? 0, _len - 1)];

        public void NextTransaction() { }

        public void Rollback() { }
    }

    private class SequentialTxProcessorWithWorldStateManager(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ILogManager logManager) : ITxProcessorWithWorldStateManager
    {
        private readonly TxProcessorWithWorldState _txProcessorWithWorldState = new(0, false, blockHashProvider, specProvider, stateProvider, logManager);

        public void Setup(Block block, BlockExecutionContext blockExecutionContext)
            => _txProcessorWithWorldState.Setup(block, blockExecutionContext);

        public TxProcessorWithWorldState Get(int? _)
            => _txProcessorWithWorldState;

        public void NextTransaction()
        {
            _txProcessorWithWorldState.WorldState.Clear();
            _txProcessorWithWorldState.WorldState.IncrementIndex();
        }

        public void Rollback() => _txProcessorWithWorldState.WorldState.Clear();
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
