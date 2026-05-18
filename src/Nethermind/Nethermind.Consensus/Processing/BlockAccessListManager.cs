// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Caching;
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
using Nethermind.Specs;
using Nethermind.State;
using static Nethermind.Consensus.Processing.BlockProcessor;
using static Nethermind.State.BlockAccessListBasedWorldState;
using RuntimeInformation = Nethermind.Core.Cpu.RuntimeInformation;

namespace Nethermind.Consensus.Processing;

public class BlockAccessListManager(
    IWorldState stateProvider,
    ISpecProvider specProvider,
    IBlockhashProvider blockHashProvider,
    ILogManager logManager,
    IBlocksConfig blocksConfig,
    IWithdrawalProcessorFactory withdrawalProcessorFactory,
    PrewarmerEnvFactory? prewarmerEnvFactory = null,
    PreBlockCaches? preBlockCaches = null,
    IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory = null)
    : IBlockAccessListManager, IDisposable
{
    private BlockExecutionContext? _blockExecutionContext;
    private ITxProcessorWithWorldStateManager? _txProcessorWithWorldStateManager;
    private readonly Lazy<ParallelTxProcessorWithWorldStateManager> _parallelTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager, prewarmerEnvFactory, preBlockCaches, readOnlyTxProcessingEnvFactory));
    private readonly Lazy<SequentialTxProcessorWithWorldStateManager> _sequentialTxProcessorWithWorldStateManager =
        new(() => new(blockHashProvider, specProvider, stateProvider, logManager));
    private const int GasValidationChunkSize = 8;
    private long? _gasRemaining;
    private bool _isBuilding;
    private bool _blockAccessListsEnabled;
    private Hash256? _parentStateRoot;
    private BlockAccessListValidationIndex? _suggestedValidationIndex;
    private BlockAccessListValidationIndex? _generatedValidationIndex;
    private int _suggestedChargeableStorageReads;
    private int _generatedChargeableStorageReads;
    private bool _hasGeneratedValidationIndexUpdates;
    private bool _hasGeneratedRequiredReadAccountMismatch;

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
            && suggestedBlock.BlockAccessList is not null
            && stateProvider.IsInScope;

        if (Enabled)
        {
            Reset();
            if (suggestedBlock.BlockAccessList is not null)
            {
                BlockAccessListValidationIndex.AddressIndex addressIndex = new();
                _suggestedValidationIndex = BlockAccessListValidationIndex.Build(suggestedBlock.BlockAccessList, suggestedBlock.Transactions.Length, addressIndex);
                _generatedValidationIndex = new(suggestedBlock.Transactions.Length, addressIndex, _suggestedValidationIndex);
                int storageReads = 0;
                foreach (AccountChanges ac in suggestedBlock.BlockAccessList.UnorderedAccountChanges)
                {
                    storageReads += BlockAccessList.CountChargeableStorageReads(ac);
                }
                _suggestedChargeableStorageReads = storageReads;
            }
            _gasRemaining = suggestedBlock.GasUsed;
            _parentStateRoot = ParallelExecutionEnabled ? stateProvider.StateRoot : null;
        }
    }

    public void Dispose()
    {
        if (_parallelTxProcessorWithWorldStateManager.IsValueCreated)
        {
            _parallelTxProcessorWithWorldStateManager.Value.Dispose();
        }
    }

    public void Setup(Block block)
    {
        if (Enabled)
        {
            _txProcessorWithWorldStateManager = ParallelExecutionEnabled ? _parallelTxProcessorWithWorldStateManager.Value : _sequentialTxProcessorWithWorldStateManager.Value;
            CheckInitialized();
            _txProcessorWithWorldStateManager.Setup(block, _blockExecutionContext.Value, _parentStateRoot);
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
            MergeAndReturnBal(0);
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
        MergeAndReturnBal(0);
        ValidateBlockAccessList(block, 0);

        long totalRegularGas = 0;
        long totalStateGas = 0;
        for (uint chunkStart = 0; chunkStart < len; chunkStart += GasValidationChunkSize)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            uint chunkEnd = Math.Min(chunkStart + GasValidationChunkSize, (uint)len);
            for (uint j = chunkStart; j < chunkEnd; j++)
            {
                Transaction tx = block.Transactions[j];

                GasValidationResult gasResult = gasResults[j].GetResult();
                // EIP-8037 per-tx 2D inclusion check (execution-specs PR 2703).
                // totalRegularGas/totalStateGas reflect the cumulatives BEFORE this tx;
                // the worst-case per-dimension contribution must fit the remaining budget.
                // The worker precomputes intrinsic gas once and carries it here to avoid
                // recalculating dynamic state-byte costs on the validation thread.
                IntrinsicGas<EthereumGasPolicy> intrinsicGas = gasResult.IntrinsicGas;
                CheckPerTxInclusion(block, (int)j, tx, _blockExecutionContext.Value.Spec, totalRegularGas, totalStateGas, in intrinsicGas);

                // Surface the worker's original tx-rejection reason before running any
                // downstream gas accounting. Otherwise CheckGasUsed can mask the true cause,
                // unlike the sequential path, which never reaches accounting on a rejected tx.
                if (gasResult.Exception is not null)
                    throw new ParallelExecutionException(gasResult.Exception);

                totalRegularGas += gasResult.BlockGasUsed;
                totalStateGas += gasResult.BlockStateGasUsed;
                SpendGas(gasResult.BlockGasUsed);

                CheckGasUsed((int)j, block, totalRegularGas, totalStateGas);

                transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs((int)j, block.Transactions[j], block.Header, receiptsTracers[j].TxReceipts[0]));

                // Worker for tx (j+1) has stashed its BAL into _perTxBal[j+1] via Return as
                // soon as the tx finished — no contention with the validator. Merge it into
                // the target now, in order, so incremental validation sees only data from
                // txs 0..(j+1).
                bool validateStorageReads = j == chunkEnd - 1;
                MergeAndReturnBal(j + 1);
                ValidateBlockAccessList(block, j + 1, validateStorageReads);
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

    internal static void CheckPerTxInclusion(Block block, int index, Transaction tx, IReleaseSpec spec, long cumulativeRegular, long cumulativeState, in IntrinsicGas<EthereumGasPolicy> intrinsic)
    {
        // EIP-8037: worst-case 2D inclusion check. Only applies when EIP-8037 is active;
        // pre-EIP-8037 blocks continue to rely solely on the post-execution running
        // max(regular, state) check.
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
            bool hasBalance = accountChanges.TryGetLastBalanceChangeBefore(Eip7928Constants.PrestateIndex, out BalanceChange balanceChange);
            bool hasNonce = accountChanges.TryGetLastNonceChangeBefore(Eip7928Constants.PrestateIndex, out NonceChange nonceChange);
            bool hasCode = accountChanges.TryGetLastCodeChangeBefore(Eip7928Constants.PrestateIndex, out CodeChange codeChange);

            if (hasBalance || hasNonce || hasCode)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);

                if (hasBalance)
                {
                    UInt256 oldBalance = stateProvider.TryGetAccount(accountChanges.Address, out AccountStruct account)
                        ? account.Balance
                        : UInt256.Zero;
                    UInt256 newBalance = balanceChange.Value;
                    if (newBalance > oldBalance)
                    {
                        stateProvider.AddToBalance(accountChanges.Address, newBalance - oldBalance, spec);
                    }
                    else
                    {
                        stateProvider.SubtractFromBalance(accountChanges.Address, oldBalance - newBalance, spec);
                    }
                }

                if (hasNonce)
                {
                    stateProvider.SetNonce(accountChanges.Address, nonceChange.Value);
                }

                if (hasCode)
                {
                    stateProvider.InsertCode(accountChanges.Address, codeChange.CodeHash, codeChange.Code, spec);
                }
            }

            foreach (SlotChanges slotChange in accountChanges.StorageChanges)
            {
                if (slotChange.Changes.TryGetLastBefore(Eip7928Constants.PrestateIndex, out StorageChange storageChange))
                {
                    StorageCell storageCell = new(accountChanges.Address, slotChange.Key);
                    // Storage cell value is stored as a byte[] in the change journal; the trimmed
                    // big-endian encoding is the canonical representation. Allocation matches the
                    // exact trimmed length; an `IWorldState.Set(ReadOnlySpan<byte>)` overload would
                    // just shift this allocation into the storage provider.
                    byte[] trimmed = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref Unsafe.AsRef(in storageChange.Value)), 32)
                        .WithoutLeadingZeros()
                        .ToArray();
                    stateProvider.Set(storageCell, trimmed);
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

            MergeAndReturnBal(uint.MaxValue);
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

        // Two fast paths exist because validation runs once per tx and is on the
        // hot per-block path. The slow path produces detailed mismatch diagnostics
        // but walks the full BAL structure, so we only fall through to it on a
        // confirmed mismatch.
        //
        // Index-based fast path: when the generated BAL has been folded into a
        // column-oriented validation index, a single ChangesEqual call compares
        // two snapshots in bulk (vector-friendly, no per-account dictionary
        // lookups). On equality, only surplus storage-read accounting remains.
        if (_hasGeneratedValidationIndexUpdates &&
            _generatedValidationIndex is not null &&
            _suggestedValidationIndex is not null)
        {
            if (_hasGeneratedRequiredReadAccountMismatch ||
                !_generatedValidationIndex.ChangesEqual(_suggestedValidationIndex, index))
            {
                ValidateBlockAccessListSlow(block, index, validateStorageReads);
            }
            else
            {
                ValidateSurplusStorageReads(block, validateStorageReads, _generatedChargeableStorageReads, _suggestedChargeableStorageReads);
            }

            return;
        }

        // Streaming fast path: no index available yet, so iterate the generated
        // and suggested account changes in lock-step counting matches/mismatches.
        // Returns false on the first divergence, which then promotes to the slow
        // path for the precise error.
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

    private void MergeAndReturnBal(uint balIndex)
    {
        int chargeableStorageReadDelta = _txProcessorWithWorldStateManager!.MergeAndReturnBal(balIndex, GeneratedBlockAccessList, RegisterGeneratedDelta);
        _generatedChargeableStorageReads += chargeableStorageReadDelta;
    }

    private void RegisterGeneratedDelta(BlockAccessList blockAccessList)
    {
        _generatedValidationIndex?.Add(blockAccessList);
        if (_suggestedValidationIndex is not null && !_hasGeneratedRequiredReadAccountMismatch)
        {
            // Read-only account that the suggested BAL didn't declare -> mismatch.
            // Skip declared state changes, system-account reads, and optional storage-only reads.
            foreach (AccountChanges generatedAccountChanges in blockAccessList.UnorderedAccountChanges)
            {
                if (generatedAccountChanges.HasStateChanges) continue;
                ChangeAtIndex generated = BlockAccessList.CreateChangeAtIndex(generatedAccountChanges, blockAccessList.Index);
                if (IsSystemAccountRead(generated, blockAccessList.Index) || HasOptionalStorageReads(generated)) continue;
                if (!_suggestedValidationIndex.HasAccount(generated.Address))
                {
                    _hasGeneratedRequiredReadAccountMismatch = true;
                    break;
                }
            }
        }

        _hasGeneratedValidationIndexUpdates = true;
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
        _parentStateRoot = null;
        _suggestedValidationIndex = null;
        _generatedValidationIndex = null;
        _suggestedChargeableStorageReads = 0;
        _generatedChargeableStorageReads = 0;
        _hasGeneratedValidationIndexUpdates = false;
        _hasGeneratedRequiredReadAccountMismatch = false;
        GeneratedBlockAccessList.Reset();
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNotInitialized(string fieldName)
        => throw new InvalidOperationException($"{fieldName} was not initialized.");

    private interface ITxProcessorWithWorldStateManager
    {
        void Setup(Block block, BlockExecutionContext blockExecutionContext, Hash256? parentStateRoot);
        TxProcessorWithWorldState Get(uint? balIndex = null);
        TxProcessorWithWorldState GetPreExecution() => Get(0);
        TxProcessorWithWorldState GetPostExecution() => Get(uint.MaxValue);
        void NextTransaction();
        void Rollback();
        int MergeAndReturnBal(uint balIndex, BlockAccessList target, Action<BlockAccessList>? beforeMerge = null);
    }

    private class ParallelTxProcessorWithWorldStateManager : ITxProcessorWithWorldStateManager, IDisposable
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
        private readonly ObjectPool<IReadOnlyTxProcessorSource>? _parentReaderEnvPool;
        private int _processorCount;
        private BlockHeader? _parentStateHeader;

        public ParallelTxProcessorWithWorldStateManager(
            IBlockhashProvider blockHashProvider,
            ISpecProvider specProvider,
            IWorldState stateProvider,
            ILogManager logManager,
            PrewarmerEnvFactory? prewarmerEnvFactory,
            PreBlockCaches? preBlockCaches,
            IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory)
        {
            _blockHashProvider = blockHashProvider;
            _specProvider = specProvider;
            _stateProvider = stateProvider;
            _logManager = logManager;
            _parentReaderEnvPool = CreateParentReaderEnvPool(prewarmerEnvFactory, preBlockCaches, readOnlyTxProcessingEnvFactory);
            for (int i = 0; i < ProcessorPoolSize; i++)
            {
                _processors.Enqueue(NewProcessor());
                _processorCount++;
            }
        }

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, Hash256? parentStateRoot)
        {
            _currentBlock = block;
            _currentCtx = blockExecutionContext;
            _parentStateHeader = null;
            if (_parentReaderEnvPool is not null)
            {
                if (parentStateRoot is null)
                {
                    ThrowNotInitialized(nameof(parentStateRoot));
                }

                _parentStateHeader = CreateParentStateHeader(block, parentStateRoot);
            }

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
            ParentReaderLease? parentReader = RentParentReader();

            try
            {
                // Install a fresh BAL before Setup so the worker has somewhere to record changes.
                processor.WorldState.SetGeneratingBlockAccessList(StaticPool<BlockAccessList>.Rent());
                processor.Setup(_currentBlock, _currentCtx, balIndex.Value, parentReader);
                _inUse[balIndex.Value] = processor;
                return processor;
            }
            catch
            {
                parentReader?.Dispose();
                if (processor.WorldState.GetGeneratingBlockAccessList() is { } generatedBal)
                {
                    StaticPool<BlockAccessList>.Return(generatedBal);
                }
                processor.WorldState.SetGeneratingBlockAccessList(null);
                ReturnProcessor(processor);
                throw;
            }
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
            processor.ClearParentReader();
            _inUse[balIndex] = null;
            ReturnProcessor(processor);
        }

        /// <summary>Merges the per-tx BAL into <paramref name="target"/> in caller-controlled
        /// order, then returns it to the pool. Idempotent w.r.t. <see cref="Return"/>: also
        /// detaches the BAL for pre/post callers that never went through Return.</summary>
        public int MergeAndReturnBal(uint balIndex, BlockAccessList target, Action<BlockAccessList>? beforeMerge = null)
        {
            balIndex = ClampBalIndex(balIndex);

            Return(balIndex);

            BlockAccessList? source = _perTxBal[balIndex];
            if (source is null) return 0;

            beforeMerge?.Invoke(source);
            int chargeableStorageReadDelta = target.MergeAndGetChargeableStorageReadCountDelta(source);
            _perTxBal[balIndex] = null;
            StaticPool<BlockAccessList>.Return(source);
            return chargeableStorageReadDelta;
        }

        public void NextTransaction() { }

        public void Rollback() { }

        private uint ClampBalIndex(uint balIndex)
            => uint.Min(balIndex, _lastBalIndex);

        private TxProcessorWithWorldState NewProcessor()
            => new(true, _blockHashProvider, _specProvider, _stateProvider, _logManager);

        private ParentReaderLease? RentParentReader()
        {
            if (_parentReaderEnvPool is null)
            {
                return null;
            }

            if (_parentStateHeader is null)
            {
                ThrowNotInitialized(nameof(_parentStateHeader));
            }

            IReadOnlyTxProcessorSource source = _parentReaderEnvPool.Get();
            try
            {
                return new ParentReaderLease(source, _parentReaderEnvPool, source.Build(_parentStateHeader));
            }
            catch
            {
                _parentReaderEnvPool.Return(source);
                throw;
            }
        }

        private static ObjectPool<IReadOnlyTxProcessorSource>? CreateParentReaderEnvPool(
            PrewarmerEnvFactory? prewarmerEnvFactory,
            PreBlockCaches? preBlockCaches,
            IReadOnlyTxProcessingEnvFactory? readOnlyTxProcessingEnvFactory)
        {
            DefaultObjectPoolProvider provider = new() { MaximumRetained = ProcessorPoolSize };
            if (prewarmerEnvFactory is not null && preBlockCaches is not null)
            {
                return provider.Create(new BlockCachePreWarmer.ReadOnlyTxProcessingEnvPooledObjectPolicy(prewarmerEnvFactory, preBlockCaches));
            }

            return readOnlyTxProcessingEnvFactory is not null
                ? provider.Create(new ReadOnlyTxProcessingEnvPooledObjectPolicy(readOnlyTxProcessingEnvFactory))
                : null;
        }

        private static BlockHeader CreateParentStateHeader(Block block, Hash256 stateRoot)
        {
            Hash256 parentHash = block.ParentHash ?? Keccak.Zero;
            return new BlockHeader(
                parentHash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                UInt256.Zero,
                block.Number - 1,
                block.GasLimit,
                0,
                [])
            {
                Hash = parentHash,
                StateRoot = stateRoot
            };
        }

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
            p.ClearParentReader();
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

        public void Dispose()
        {
            int previousSize = (int)_lastBalIndex + 1;
            ReclaimAndResize(_inUse.Length, previousSize);
            (_parentReaderEnvPool as IDisposable)?.Dispose();
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

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, Hash256? parentStateRoot)
            => _txProcessorWithWorldState.Setup(block, blockExecutionContext, 0, null);

        public TxProcessorWithWorldState Get(uint? _)
            => _txProcessorWithWorldState;

        public void NextTransaction()
        {
            _txProcessorWithWorldState.WorldState.Clear();
            _txProcessorWithWorldState.WorldState.IncrementIndex();
        }

        public void Rollback() => _txProcessorWithWorldState.WorldState.Clear();

        public int MergeAndReturnBal(uint _, BlockAccessList target, Action<BlockAccessList>? beforeMerge = null)
        {
            BlockAccessList? source = _txProcessorWithWorldState.WorldState.GetGeneratingBlockAccessList();
            if (source is null)
            {
                return 0;
            }

            beforeMerge?.Invoke(source);
            return target.MergeAndGetChargeableStorageReadCountDelta(source);
        }
    }

    private class TxProcessorWithWorldState
    {
        public readonly TracedAccessWorldState WorldState;
        public readonly TransactionProcessor<EthereumGasPolicy> TxProcessor;
        public readonly ExecuteTransactionProcessorAdapter TxProcessorAdapter;
        private readonly BlockAccessListBasedWorldState? _balWorldState;
        private ParentReaderLease? _parentReader;

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

        public void Setup(Block block, BlockExecutionContext blockExecutionContext, uint balIndex, ParentReaderLease? parentReader)
        {
            if (_parentReader is not null)
            {
                ThrowParentReaderStillAttached();
            }

            _parentReader = parentReader;
            WorldState.Clear();
            WorldState.SetIndex(balIndex);
            _balWorldState?.SetBlockAccessIndex(balIndex);
            TxProcessorAdapter.SetBlockExecutionContext(blockExecutionContext);
            if (_balWorldState is not null)
            {
                if (parentReader is null)
                {
                    ThrowParentReaderUnavailable();
                }

                _balWorldState.SetParentReader(parentReader.WorldState);
                _balWorldState.Setup(block);
            }
        }

        public void ClearParentReader()
        {
            _balWorldState?.ClearParentReader();
            _parentReader?.Dispose();
            _parentReader = null;
        }
    }

    // RAII wrapper around a borrowed read-only tx-processing env: holds the
    // pooled source plus the scope built against the parent state root, and
    // returns the source to its pool when disposed. Used by parallel workers
    // so each tx gets its own snapshot reader without contending on the
    // mutable state provider.
    private sealed class ParentReaderLease(
        IReadOnlyTxProcessorSource source,
        ObjectPool<IReadOnlyTxProcessorSource> envPool,
        IReadOnlyTxProcessingScope scope) : IDisposable
    {
        private IReadOnlyTxProcessorSource? _source = source;
        private IReadOnlyTxProcessingScope? _scope = scope;

        public IWorldState WorldState => _scope?.WorldState ?? ThrowDisposed();

        public void Dispose()
        {
            IReadOnlyTxProcessorSource? source = _source;
            IReadOnlyTxProcessingScope? scope = _scope;
            if (source is null || scope is null)
            {
                return;
            }

            _source = null;
            _scope = null;
            scope.Dispose();
            envPool.Return(source);
        }

        [DoesNotReturn, StackTraceHidden]
        private static IWorldState ThrowDisposed()
            => throw new ObjectDisposedException(nameof(ParentReaderLease));
    }

    private sealed class ReadOnlyTxProcessingEnvPooledObjectPolicy(IReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => envFactory.Create();

        public bool Return(IReadOnlyTxProcessorSource obj) => true;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowParentReaderStillAttached()
        => throw new InvalidOperationException("A parent state reader is already attached to the pooled BAL transaction processor.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowParentReaderUnavailable()
        => throw new InvalidOperationException("Parallel BAL validation requires a parent state reader. Construct BlockAccessListManager through DI or pass an IReadOnlyTxProcessingEnvFactory.");
}
