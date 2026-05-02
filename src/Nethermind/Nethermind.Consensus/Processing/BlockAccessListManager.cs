// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Linq;
using Nethermind.Blockchain;
using System.Collections.Concurrent;
using Nethermind.Core.Caching;
using Nethermind.Core.Cpu;
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
                ?? TransactionResult.ErrorType.MalformedTransaction.WithDetail($"Parallel execution failure: {innerException.Message}"),
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

    public void ReturnTxProcessor(int balIndex)
    {
        if (Enabled && ParallelExecutionEnabled)
        {
            // Eagerly detach the worker's generated BAL into a per-tx slot and recycle the
            // pool slot. Workers therefore never block on the validator — but the validator
            // still merges per-tx slots into the target in order, preserving incremental
            // validation semantics.
            _parallelTxProcessorWithWorldStateManager.Return(balIndex);
        }
    }

    public void IncrementalValidation(Block block, TaskCompletionSource<(long BlockGasUsed, long BlockStateGasUsed, InvalidBlockException? Exception)>[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token)
    {
        CheckInitialized();

        int len = block.Transactions.Length;

        // Pre is held by main-thread system contract handlers — never went through Return,
        // so MergeAndReturnBal will detach it before merging.
        _parallelTxProcessorWithWorldStateManager.MergeAndReturnBal(0, GeneratedBlockAccessList);
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

                // Worker for tx (j+1) has stashed its BAL into _perTxBal[j+1] via Return as
                // soon as the tx finished — no contention with the validator. Merge it into
                // the target now, in order, so incremental validation sees only data from
                // txs 0..(j+1).
                bool validateStorageReads = j == chunkEnd - 1;
                _parallelTxProcessorWithWorldStateManager.MergeAndReturnBal(j + 1, GeneratedBlockAccessList);
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

            _txProcessorWithWorldStateManager.MergeAndReturnBal(int.MaxValue, GeneratedBlockAccessList);
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
        if (_txProcessorWithWorldStateManager is null) ThrowNotInitialized(nameof(_txProcessorWithWorldStateManager));
        if (_gasRemaining is null) ThrowNotInitialized(nameof(_gasRemaining));
        if (_blockExecutionContext is null) ThrowNotInitialized(nameof(_blockExecutionContext));
    }

    [DoesNotReturn]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");

    private interface ITxProcessorWithWorldStateManager
    {
        void Setup(Block block, BlockExecutionContext blockExecutionContext);
        TxProcessorWithWorldState Get(int? balIndex = null);
        TxProcessorWithWorldState GetPreExecution() => Get(0);
        TxProcessorWithWorldState GetPostExecution() => Get(int.MaxValue);
        void NextTransaction();
        void Rollback();
        void MergeAndReturnBal(int balIndex, BlockAccessList target);
    }

    private class ParallelTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager
    {
        private const int DefaultTxCount = 10000;
        private static readonly int ProcessorPoolSize = RuntimeInformation.ProcessorCount;

        // BAL pool is larger since extra BALs are retained so they can be merged in order
        private static readonly int BalPoolSize = RuntimeInformation.ProcessorCount * 2;

        static ParallelTxProcessorWithWorldStateManager()
        {
            StaticPool<BlockAccessList>.SetMaxPooledCount(BalPoolSize);
            for (int i = 0; i < BalPoolSize; i++)
            {
                StaticPool<BlockAccessList>.Return(new());
            }
        }

        private Block? _currentBlock;
        private BlockExecutionContext _currentCtx;
        private int _lastBalIndex;

        // _inUse[i] is the processor currently bound to balIndex i.
        private TxProcessorWithWorldState?[] _inUse = new TxProcessorWithWorldState?[DefaultTxCount];

        // _perTxBal[i] holds its detached BAL until the validator merges it in order.
        private BlockAccessList?[] _perTxBal = new BlockAccessList?[DefaultTxCount];

        // processors are not shared statically between BAL managers
        private readonly ConcurrentQueue<TxProcessorWithWorldState> _processors = [];
        private readonly IBlockhashProvider _blockHashProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IWorldState _stateProvider;
        private readonly ILogManager _logManager;
        private int _processorCount;

        public ParallelTxProcessorWithWorldStateManager(
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager)
        {
            _blockHashProvider = blockHashProvider;
            _specProvider = specProvider;
            _stateProvider = stateProvider;
            _logManager = logManager;
            for (int i = 0; i < ProcessorPoolSize; i++)
            {
                _processors.Enqueue(NewProcessor());
                _processorCount++;
            }
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext)
        {
            _currentBlock = block;
            _currentCtx = blockExecutionContext;

            int previousSize = _lastBalIndex + 1;
            int newLastBalIndex = block.Transactions.Length + 1;
            ReclaimAndResize(newLastBalIndex + 1, previousSize);
            _lastBalIndex = newLastBalIndex;
        }

        // Thread-safety note for _inUse / _perTxBal:
        //   Each balIndex slot has at most one writer at a time. Pre/post (idx 0 and
        //   _lastBalIndex) are written only by the main thread; tx slots (1..len) are
        //   each owned by a single parallel-loop iteration, so no two workers ever
        //   touch the same slot. Cross-thread reads (validator → worker's slot) happen
        //   strictly after the worker's gasResults[i-1].SetResult, whose pairing with
        //   GetResult() establishes the publication barrier. Plain reads/writes are
        //   therefore sufficient — Volatile/Interlocked would be redundant fencing.
        public TxProcessorWithWorldState Get(int? balIndex = null)
        {
            if (_currentBlock is null) ThrowNotInitialized(nameof(_currentBlock));

            balIndex = ClampBalIndex(balIndex ?? 0);

            // Re-entrant Get for the same balIndex returns the already-acquired processor
            // (lets pre/post callers share state across calls — main thread only).
            TxProcessorWithWorldState? existing = _inUse[balIndex.Value];
            if (existing is not null) return existing;

            TxProcessorWithWorldState processor = RentProcessor();

            // Install a fresh BAL before Setup so the worker has somewhere to record changes.
            processor.WorldState.SetGeneratingBlockAccessList(StaticPool<BlockAccessList>.Rent());
            processor.Setup(_currentBlock, _currentCtx, balIndex.Value);
            _inUse[balIndex.Value] = processor;
            return processor;
        }

        /// <summary>Detaches the worker's populated BAL into the per-tx slot and recycles
        /// the processor immediately, so workers never block on the validator.</summary>
        public void Return(int balIndex)
        {
            balIndex = ClampBalIndex(balIndex);

            TxProcessorWithWorldState? processor = _inUse[balIndex];
            if (processor is null) return;

            _perTxBal[balIndex] = processor.WorldState.GetGeneratingBlockAccessList();
            processor.WorldState.SetGeneratingBlockAccessList(null);
            _inUse[balIndex] = null;
            ReturnProcessor(processor);
        }

        /// <summary>Merges the per-tx BAL into <paramref name="target"/> in caller-controlled
        /// order, then returns it to the pool. Idempotent w.r.t. <see cref="Return"/>: also
        /// detaches the BAL for pre/post callers that never went through Return.</summary>
        public void MergeAndReturnBal(int balIndex, BlockAccessList target)
        {
            balIndex = ClampBalIndex(balIndex);

            Return(balIndex);

            BlockAccessList? source = _perTxBal[balIndex];
            if (source is null) return;

            target.Merge(source);
            _perTxBal[balIndex] = null;
            StaticPool<BlockAccessList>.Return(source);
        }

        public void NextTransaction() { }

        public void Rollback() { }

        private int ClampBalIndex(int balIndex)
            => int.Min(int.Max(balIndex, 0), _lastBalIndex);

        private TxProcessorWithWorldState NewProcessor()
            => new(true, _blockHashProvider, _specProvider, _stateProvider, _logManager);

        private TxProcessorWithWorldState RentProcessor()
        {
            if (Volatile.Read(ref _processorCount) > 0 && _processors.TryDequeue(out TxProcessorWithWorldState? p))
            {
                Interlocked.Decrement(ref _processorCount);
                return p;
            }
            return NewProcessor();
        }

        private void ReturnProcessor(TxProcessorWithWorldState p)
        {
            if (Interlocked.Increment(ref _processorCount) > ProcessorPoolSize)
            {
                Interlocked.Decrement(ref _processorCount);
                return;
            }
            _processors.Enqueue(p);
        }

        private void ReclaimAndResize(int size, int previousSize)
        {
            for (int i = 0; i < previousSize; i++)
                if (_inUse[i] is not null) Return(i);

            for (int i = 0; i < previousSize; i++)
            {
                if (_perTxBal[i] is { } bal)
                {
                    StaticPool<BlockAccessList>.Return(bal);
                    _perTxBal[i] = null;
                }
            }

            if (_inUse.Length < size)
                Array.Resize(ref _inUse, Math.Max(2 * _inUse.Length, size));
            if (_perTxBal.Length < size)
                Array.Resize(ref _perTxBal, Math.Max(2 * _perTxBal.Length, size));
        }

    }

    private class SequentialTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager
    {
        private readonly TxProcessorWithWorldState _txProcessorWithWorldState;

        public SequentialTxProcessorWithWorldStateManager(
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager)
        {
            _txProcessorWithWorldState = new(false, blockHashProvider, specProvider, stateProvider, logManager);
            _txProcessorWithWorldState.WorldState.SetGeneratingBlockAccessList(new());
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext)
            => _txProcessorWithWorldState.Setup(block, blockExecutionContext, 0);

        public TxProcessorWithWorldState Get(int? _)
            => _txProcessorWithWorldState;

        public void NextTransaction()
        {
            _txProcessorWithWorldState.WorldState.Clear();
            _txProcessorWithWorldState.WorldState.IncrementIndex();
        }

        public void Rollback() => _txProcessorWithWorldState.WorldState.Clear();

        public void MergeAndReturnBal(int _, BlockAccessList target)
            => _txProcessorWithWorldState.WorldState.MergeGeneratingBal(target);
    }

    private class TxProcessorWithWorldState
    {
        public readonly TracedAccessWorldState WorldState;
        public readonly TransactionProcessor<EthereumGasPolicy> TxProcessor;
        public readonly ExecuteTransactionProcessorAdapter TxProcessorAdapter;
        private readonly BlockAccessListBasedWorldState? _balWorldState;

        public TxProcessorWithWorldState(
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
                _balWorldState = new BlockAccessListBasedWorldState(stateProvider, logManager);
                worldState = _balWorldState;
            }
            WorldState = new TracedAccessWorldState(worldState, parallel);
            EthereumCodeInfoRepository codeInfoRepository = new(WorldState);
            TxProcessor = new(BlobBaseFeeCalculator.Instance, specProvider, WorldState, virtualMachine, codeInfoRepository, logManager, parallel);
            TxProcessorAdapter = new(TxProcessor);
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, int balIndex)
        {
            WorldState.Clear();
            WorldState.SetIndex(balIndex);
            _balWorldState?.SetBlockAccessIndex(balIndex);
            TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
            _balWorldState?.Setup(block);
        }
    }
}
