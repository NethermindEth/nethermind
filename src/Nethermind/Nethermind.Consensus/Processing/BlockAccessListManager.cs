// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using System.Collections.Concurrent;
using Nethermind.Core.Caching;
using Nethermind.Core.Cpu;
using Nethermind.Blockchain.BeaconBlockRoot;
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
    private BlockExecutionContext? _blockExecutionContext;
    private ITxProcessorWithWorldStateManager? _txProcessorWithWorldStateManager;
    private readonly Lazy<ParallelTxProcessorWithWorldStateManager> _parallelTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager));
    private readonly Lazy<SequentialTxProcessorWithWorldStateManager> _sequentialTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager));
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

    public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)
    {
        _blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled;
        Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis;
        _isBuilding = options.ContainsFlag(ProcessingOptions.ProducingBlock);

        // Parallel execution requires the BAL body to be present on the block.
        // Blocks from p2p/RLP fixtures only have the header hash, not the decoded BAL body.
        ParallelExecutionEnabled = Enabled
            && blocksConfig.ParallelExecution
            && !_isBuilding
            && suggestedBlock.BlockAccessList is not null;

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
            _txProcessorWithWorldStateManager = ParallelExecutionEnabled ? _parallelTxProcessorWithWorldStateManager.Value : _sequentialTxProcessorWithWorldStateManager.Value;
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

    public ITransactionProcessorAdapter GetTxProcessor(uint? balIndex = null)
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

    public void ReturnTxProcessor(uint balIndex)
    {
        if (Enabled && ParallelExecutionEnabled)
        {
            // Eagerly detach the worker's generated BAL into a per-tx slot and recycle the
            // pool slot. Workers therefore never block on the validator — but the validator
            // still merges per-tx slots into the target in order, preserving incremental
            // validation semantics.
            _parallelTxProcessorWithWorldStateManager.Value.Return(balIndex);
        }
    }

    public void IncrementalValidation(Block block, GasValidationResultSlot[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token)
    {
        CheckInitialized();

        int len = block.Transactions.Length;

        // Pre is held by main-thread system contract handlers — never went through Return,
        // so MergeAndReturnBal will detach it before merging.
        _txProcessorWithWorldStateManager.MergeAndReturnBal(0, GeneratedBlockAccessList);
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
                Transaction tx = block.Transactions[j];

                GasValidationResult gasResult = gasResults[j].GetResult();
                // EIP-8037 per-tx 2D inclusion check (execution-specs PR 2703).
                // totalRegularGas/totalStateGas reflect the cumulatives BEFORE this tx;
                // the worst-case per-dimension contribution must fit the remaining budget.
                // The worker precomputes intrinsic gas once and carries it here to avoid
                // recalculating dynamic state-byte costs on the validation thread.
                CheckPerTxInclusion(block, j, tx, _blockExecutionContext.Value.Spec, totalRegularGas, totalStateGas, in gasResult.IntrinsicGas);

                // Surface the worker's original tx-rejection reason before running any
                // downstream gas accounting. Otherwise CheckGasUsed can mask the true cause,
                // unlike the sequential path, which never reaches accounting on a rejected tx.
                if (gasResult.Exception is not null)
                    throw new ParallelExecutionException(gasResult.Exception);

                totalRegularGas += gasResult.BlockGasUsed;
                totalStateGas += gasResult.BlockStateGasUsed;
                SpendGas(gasResult.BlockGasUsed);

                CheckGasUsed(j, block, totalRegularGas, totalStateGas);

                transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(j, block.Transactions[j], block.Header, receiptsTracers[j].TxReceipts[0]));

                // Worker for tx (j+1) has stashed its BAL into _perTxBal[j+1] via Return as
                // soon as the tx finished — no contention with the validator. Merge it into
                // the target now, in order, so incremental validation sees only data from
                // txs 0..(j+1).
                bool validateStorageReads = j == chunkEnd - 1;
                _txProcessorWithWorldStateManager.MergeAndReturnBal((uint)(j + 1), GeneratedBlockAccessList);
                ValidateBlockAccessList(block, (uint)(j + 1), validateStorageReads);
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

    internal static void CheckPerTxInclusion(Block block, int index, Transaction tx, IReleaseSpec spec, long cumulativeRegular, long cumulativeState)
    {
        // EIP-8037 (bal-devnet-6, execution-specs PR 2703): worst-case 2D inclusion
        // check. Only applies when EIP-8037 is active; legacy and pre-EIP-8037 blocks
        // continue to rely solely on the post-execution running max(R,S) check.
        if (!spec.IsEip8037Enabled) return;

        IntrinsicGas<EthereumGasPolicy> intrinsic = EthereumGasPolicy.CalculateIntrinsicGas(tx, spec, block.Header.GasLimit);
        CheckPerTxInclusion(block, index, tx, spec, cumulativeRegular, cumulativeState, in intrinsic);
    }

    internal static void CheckPerTxInclusion(Block block, int index, Transaction tx, IReleaseSpec spec, long cumulativeRegular, long cumulativeState, in IntrinsicGas<EthereumGasPolicy> intrinsic)
    {
        // EIP-8037 (bal-devnet-6, execution-specs PR 2703): worst-case 2D inclusion
        // check. Only applies when EIP-8037 is active; legacy and pre-EIP-8037 blocks
        // continue to rely solely on the post-execution running max(R,S) check.
        if (!spec.IsEip8037Enabled) return;

        long intrinsicRegular = intrinsic.Standard.Value;
        long intrinsicState = intrinsic.Standard.StateReservoir;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            block.Header.GasLimit,
            cumulativeRegular,
            cumulativeState,
            tx.GasLimit,
            intrinsicRegular,
            intrinsicState);

        if (outcome != Eip8037BlockGasInclusionCheck.Outcome.Ok)
        {
            throw new InvalidBlockException(block,
                $"Block gas limit exceeded: tx {index} fails EIP-8037 inclusion check ({outcome}); " +
                $"regular_available={block.Header.GasLimit - cumulativeRegular}, " +
                $"state_available={block.Header.GasLimit - cumulativeState}, " +
                $"tx.gas={tx.GasLimit}, intrinsic.regular={intrinsicRegular}, intrinsic.state={intrinsicState}.");
        }
    }

    public static void ApplyStateChanges(BlockAccessList suggestedBlockAccessList, IWorldState stateProvider, IReleaseSpec spec, bool shouldComputeStateRoot)
    {
        foreach (AccountChanges accountChanges in suggestedBlockAccessList.AccountChangesByAddress)
        {
            if (accountChanges.BalanceChanges.Count > 0 && accountChanges.BalanceChanges[^1].Index != Eip7928Constants.PrestateIndex)
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

            if (accountChanges.NonceChanges.Count > 0 && accountChanges.NonceChanges[^1].Index != Eip7928Constants.PrestateIndex)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                stateProvider.SetNonce(accountChanges.Address, accountChanges.NonceChanges[^1].Value);
            }

            if (accountChanges.CodeChanges.Count > 0 && accountChanges.CodeChanges[^1].Index != Eip7928Constants.PrestateIndex)
            {
                stateProvider.InsertCode(accountChanges.Address, accountChanges.CodeChanges[^1].Code, spec);
            }

            foreach (SlotChanges slotChange in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, slotChange.Key);
                // could be empty since prestate loaded
                int slotCount = slotChange.Changes.Count;
                if (slotCount > 0 && slotChange.Changes.Keys[slotCount - 1] != Eip7928Constants.PrestateIndex)
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

            _txProcessorWithWorldStateManager.MergeAndReturnBal(uint.MaxValue, GeneratedBlockAccessList);
            block.GeneratedBlockAccessList = GeneratedBlockAccessList;
            block.EncodedBlockAccessList = Rlp.Encode(GeneratedBlockAccessList).Bytes;
            block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
        }
    }

    public void ValidateBlockAccessList(Block block, uint index, bool validateStorageReads = true)
    {
        BlockAccessList? suggestedBlockAccessList = block.BlockAccessList;
        if (suggestedBlockAccessList is null)
        {
            return;
        }

        CheckInitialized();

        if (!TryValidateBlockAccessListFast(block, suggestedBlockAccessList, index, validateStorageReads))
        {
            ValidateBlockAccessListSlow(block, index, validateStorageReads);
        }
    }

    private bool TryValidateBlockAccessListFast(Block block, BlockAccessList suggestedBlockAccessList, uint index, bool validateStorageReads)
    {
        int generatedReads = 0;
        int suggestedReads = 0;
        int matchedGeneratedAccounts = 0;
        int suggestedAccountCount = suggestedBlockAccessList.UnorderedAccountChanges.Count;

        foreach (AccountChanges generatedAccountChanges in GeneratedBlockAccessList.UnorderedAccountChanges)
        {
            ChangeAtIndex generated = BlockAccessList.CreateChangeAtIndex(generatedAccountChanges, index);
            generatedReads += generated.Reads;

            AccountChanges? suggestedAccountChanges = suggestedBlockAccessList.GetAccountChanges(generated.Address);
            if (suggestedAccountChanges is null)
            {
                if (IsSystemAccountRead(generated, index) || HasOptionalStorageReads(generated))
                {
                    continue;
                }

                return false;
            }

            matchedGeneratedAccounts++;
            ChangeAtIndex suggested = BlockAccessList.CreateChangeAtIndex(suggestedAccountChanges, index);
            suggestedReads += suggested.Reads;

            if (!ChangesAtIndexEqual(generated, suggested, index))
            {
                return false;
            }
        }

        if (matchedGeneratedAccounts != suggestedAccountCount)
        {
            foreach (AccountChanges suggestedAccountChanges in suggestedBlockAccessList.UnorderedAccountChanges)
            {
                if (GeneratedBlockAccessList.HasAccount(suggestedAccountChanges.Address))
                {
                    continue;
                }

                ChangeAtIndex suggested = BlockAccessList.CreateChangeAtIndex(suggestedAccountChanges, index);
                suggestedReads += suggested.Reads;

                if (!HasNoChanges(suggested))
                {
                    return false;
                }
            }
        }

        ValidateSurplusStorageReads(block, validateStorageReads, generatedReads, suggestedReads);
        return true;
    }

    private void ValidateBlockAccessListSlow(Block block, uint index, bool validateStorageReads)
    {
        if (block.BlockAccessList is null)
        {
            return;
        }

        ChangesAtIndexEnumerable.Enumerator generatedChanges = GeneratedBlockAccessList.GetChangesAtIndex(index).GetEnumerator();
        ChangesAtIndexEnumerable.Enumerator suggestedChanges = block.BlockAccessList.GetChangesAtIndex(index).GetEnumerator();

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
                    !generatedHead.Value.AccountChanges.SlotChangesAtIndexEqual(suggestedHead.Value.AccountChanges, index))
                {
                    throw new InvalidBlockLevelAccessListException(block.Header,
                        $"Suggested block-level access list contained incorrect changes for {suggestedHead.Value.Address} at index {index}. " +
                        $"Generated: {DescribeChange(generatedHead.Value)}. Suggested: {DescribeChange(suggestedHead.Value)}.");
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

        ValidateSurplusStorageReads(block, validateStorageReads, generatedReads, suggestedReads);

        static string DescribeChange(ChangeAtIndex change)
        {
            string slotChanges = change.AccountChanges.DescribeSlotChangesAtIndex(change.Index);
            return $"{change.Address} Balance={change.BalanceChange?.ToString() ?? "-"} Nonce={change.NonceChange?.ToString() ?? "-"} Code={change.CodeChange?.ToString() ?? "-"} Slots=[{slotChanges}] Reads={change.Reads}";
        }
    }

    private void ValidateSurplusStorageReads(Block block, bool validateStorageReads, int generatedReads, int suggestedReads)
    {
        int surplusSuggestedReads = suggestedReads - generatedReads;
        if (validateStorageReads && surplusSuggestedReads > 0 && _gasRemaining < surplusSuggestedReads * Eip7928Constants.ItemCost)
        {
            throw new InvalidBlockLevelAccessListException(block.Header, "Suggested block-level access list contained invalid storage reads.");
        }
    }

    private static bool ChangesAtIndexEqual(in ChangeAtIndex generated, in ChangeAtIndex suggested, uint index) =>
        generated.BalanceChange == suggested.BalanceChange &&
        generated.NonceChange == suggested.NonceChange &&
        generated.CodeChange.HasValue == suggested.CodeChange.HasValue &&
        (generated.CodeChange is null || generated.CodeChange.Value.Equals(suggested.CodeChange.Value)) &&
        generated.AccountChanges.SlotChangesAtIndexEqual(suggested.AccountChanges, index);

    public void StoreBeaconRoot(Block block, IReleaseSpec spec)
    {
        CheckInitialized();

        TxProcessorWithWorldState preExecution = _txProcessorWithWorldStateManager.GetPreExecution();
        new BeaconBlockRootHandler(preExecution.TxProcessor, preExecution.WorldState).StoreBeaconRoot(block, spec, NullTxTracer.Instance);
    }

    public void ApplyBlockhashStateChanges(BlockHeader header, IReleaseSpec spec)
    {
        CheckInitialized();

        TxProcessorWithWorldState preExecution = _txProcessorWithWorldStateManager.GetPreExecution();
        new BlockhashStore(preExecution.WorldState).ApplyBlockhashStateChanges(header, spec);
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
        // Wire BAL validation must run before this method: it appends local-only
        // PrestateIndex entries that are sorted before real tx indices and must not be
        // subjected to block-level wire index-bounds validation.
        foreach (AccountChanges accountChanges in bal.AccountChangesByAddress)
        {
            // check if changed before loading prestate
            accountChanges.CheckWasChanged();

            bool exists = stateProvider.TryGetAccount(accountChanges.Address, out AccountStruct account);
            accountChanges.ExistedBeforeBlock = exists;
            accountChanges.EmptyBeforeBlock = !account.HasStorage;

            accountChanges.AddBalanceChange(new(Eip7928Constants.PrestateIndex, account.Balance));
            accountChanges.AddNonceChange(new(Eip7928Constants.PrestateIndex, (ulong)account.Nonce));
            accountChanges.AddCodeChange(new(Eip7928Constants.PrestateIndex, stateProvider.GetCode(accountChanges.Address)));

            foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, slotChanges.Key);
                slotChanges.AddStorageChange(new(Eip7928Constants.PrestateIndex, new(stateProvider.Get(storageCell), true)));
            }

            foreach (UInt256 storageRead in accountChanges.StorageReads)
            {
                SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(storageRead);
                StorageCell storageCell = new(accountChanges.Address, storageRead);
                slotChanges.AddStorageChange(new(Eip7928Constants.PrestateIndex, new(stateProvider.Get(storageCell), true)));
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

    private static bool IsSystemAccountRead(in ChangeAtIndex c, uint index)
        => index == 0 && c.Address == Address.SystemUser && HasNoChanges(c) && c.Reads == 0;

    private void CheckInitialized()
    {
        if (_txProcessorWithWorldStateManager is null) ThrowNotInitialized(nameof(_txProcessorWithWorldStateManager));
        if (_gasRemaining is null) ThrowNotInitialized(nameof(_gasRemaining));
        if (_blockExecutionContext is null) ThrowNotInitialized(nameof(_blockExecutionContext));
    }

    private void Reset()
    {
        _txProcessorWithWorldStateManager = null;
        _blockExecutionContext = null;
        _gasRemaining = null;
        GeneratedBlockAccessList.Reset();
    }

    [DoesNotReturn]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");

    private interface ITxProcessorWithWorldStateManager
    {
        void Setup(Block block, BlockExecutionContext blockExecutionContext);
        TxProcessorWithWorldState Get(uint? balIndex = null);
        TxProcessorWithWorldState GetPreExecution() => Get(0);
        TxProcessorWithWorldState GetPostExecution() => Get(uint.MaxValue);
        void NextTransaction();
        void Rollback();
        void MergeAndReturnBal(uint balIndex, BlockAccessList target);
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
        private uint _lastBalIndex;

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

            int previousSize = (int)_lastBalIndex + 1;
            uint newLastBalIndex = (uint)block.Transactions.Length + 1;
            ReclaimAndResize((int)newLastBalIndex + 1, previousSize);
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
        public TxProcessorWithWorldState Get(uint? balIndex = null)
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
        public void Return(uint balIndex)
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
        public void MergeAndReturnBal(uint balIndex, BlockAccessList target)
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

        private uint ClampBalIndex(uint balIndex)
            => uint.Min(balIndex, _lastBalIndex);

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
                if (_inUse[i] is not null) Return((uint)i);

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

        public TxProcessorWithWorldState Get(uint? _)
            => _txProcessorWithWorldState;

        public void NextTransaction()
        {
            _txProcessorWithWorldState.WorldState.Clear();
            _txProcessorWithWorldState.WorldState.IncrementIndex();
        }

        public void Rollback() => _txProcessorWithWorldState.WorldState.Clear();

        public void MergeAndReturnBal(uint _, BlockAccessList target)
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

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex)
        {
            WorldState.Clear();
            WorldState.SetIndex(balIndex);
            _balWorldState?.SetBlockAccessIndex(balIndex);
            TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
            _balWorldState?.Setup(block);
        }
    }
}
