// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using static Nethermind.Consensus.Processing.BlockProcessor;
using static Nethermind.State.BlockAccessListBasedWorldState;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Incremental + per-tx validation paths. Hot during block processing:
///   * <see cref="IncrementalValidation"/> awaits each worker's gas result, runs the
///     EIP-8037 2D inclusion check on the cumulative gas, then merges the per-tx BAL slice
///     and calls <see cref="ValidateBlockAccessList"/>.
///   * <see cref="CheckPerTxInclusion"/> is the EIP-8037 worst-case 2D inclusion check.
///   * <see cref="ValidateBlockAccessList"/> tries the column-index fast path first and
///     falls through to a precise streaming comparison on mismatch.
/// </summary>
public partial class BlockAccessListManager
{
    public void IncrementalValidation(Block block, GasValidationResultSlot[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token)
    {
        CheckInitialized();

        int len = block.Transactions.Length;

        // balIndex 0 (pre-execution) is held by main-thread system contract handlers — never
        // went through Return, so MergeAndReturnBal will detach it before merging.
        MergeAndReturnBal(0u);
        ValidateBlockAccessList(block, 0u);

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
                IntrinsicGas<EthereumGasPolicy> intrinsicGas = gasResult.IntrinsicGas;
                // EIP-8037 per-tx 2D inclusion check (execution-specs PR 2703).
                // totalRegularGas/totalStateGas reflect the cumulatives BEFORE this tx;
                // the worst-case per-dimension contribution must fit the remaining budget.
                // The worker precomputes intrinsic gas once and carries it here to avoid
                // recalculating dynamic state-byte costs on the validation thread.
                CheckPerTxInclusion(block, j, tx, _blockExecutionContext.Value.Spec, totalRegularGas, totalStateGas, in intrinsicGas);

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
                MergeAndReturnBal((uint)(j + 1));
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

    public void ValidateBlockAccessList(Block block, uint index, bool validateStorageReads = true)
    {
        if (block.BlockAccessList is null)
        {
            return;
        }

        CheckInitialized();

        // Fast path: when the column-oriented validation index is populated for this index,
        // a single ChangesEqual call compares both sides row-by-row in bulk. On match, only
        // the surplus-storage-reads gas check remains — no per-account dict lookups, no
        // sorted merge walk. On mismatch (or when the index isn't ready, or when a generated
        // slice contained a read-only account the suggested side never declared — invisible
        // to ChangesEqual), fall through to the streaming walk below which produces precise
        // diagnostics.
        if (_hasGeneratedValidationIndexUpdates &&
            _suggestedValidationIndex is not null &&
            _generatedValidationIndex is not null &&
            !_hasGeneratedRequiredReadAccountMismatch &&
            _generatedValidationIndex.ChangesEqual(_suggestedValidationIndex, index))
        {
            int fastSurplus = _suggestedChargeableStorageReads - _generatedChargeableStorageReads;
            if (validateStorageReads && fastSurplus > 0 && _gasRemaining < fastSurplus * Eip7928Constants.ItemCost)
            {
                throw new InvalidBlockLevelAccessListException(block.Header, "Suggested block-level access list contained invalid storage reads.");
            }
            return;
        }

        GeneratedBlockAccessList generated = GeneratedBlockAccessList;
        ReadOnlyBlockAccessList suggested = block.BlockAccessList;

        int generatedReads = 0;
        int suggestedReads = 0;

        // Pass 1: walk generated; for each account, look up the matching entry in suggested
        // via the dictionary (O(1)) instead of a sorted merge-walk. Catches "missing-from-
        // suggested" and "incorrect changes at this index".
        foreach (GeneratedAccountChanges gen in generated.AccountChanges)
        {
            int genReads = IsSystemContract(gen.Address) ? 0 : gen.StorageReads.Count;
            generatedReads += genReads;

            ReadOnlyAccountChanges? sug = suggested.GetAccountChanges(gen.Address);
            if (sug is not null)
            {
                if (!gen.ChangesAtIndexEqual(sug, index))
                {
                    throw new InvalidBlockLevelAccessListException(block.Header,
                        $"Suggested block-level access list contained incorrect changes for {gen.Address} at index {index}.");
                }
                continue;
            }

            // Generated has the account, suggested doesn't. Tolerated only when there are no
            // changes at this index AND the entry is either a system-user read at index 0 or
            // a generic storage-read-only entry.
            if (gen.HasNoChangesAtIndex(index) &&
                ((index == 0 && gen.Address == Address.SystemUser && genReads == 0) || genReads > 0))
            {
                continue;
            }

            throw new InvalidBlockLevelAccessListException(block.Header,
                $"Suggested block-level access list missing account changes for {gen.Address} at index {index}.");
        }

        // Pass 2: walk suggested; only accounts NOT present in generated need attention.
        // Tally suggested reads here for the storage-read gas-budget check below.
        foreach (ReadOnlyAccountChanges sug in suggested.AccountChanges)
        {
            suggestedReads += IsSystemContract(sug.Address) ? 0 : sug.StorageReads.Length;

            if (generated.HasAccount(sug.Address))
            {
                continue;
            }

            if (!sug.HasNoChangesAtIndex(index))
            {
                throw new InvalidBlockLevelAccessListException(block.Header,
                    $"Suggested block-level access list contained surplus changes for {sug.Address} at index {index}.");
            }
        }

        int surplusSuggestedReads = suggestedReads - generatedReads;
        if (validateStorageReads && surplusSuggestedReads > 0 && _gasRemaining < surplusSuggestedReads * Eip7928Constants.ItemCost)
        {
            throw new InvalidBlockLevelAccessListException(block.Header, "Suggested block-level access list contained invalid storage reads.");
        }
    }

    /// <summary>
    /// Hook called by <see cref="ITxProcessorWithWorldStateManager.MergeAndReturnBal"/> after
    /// each per-tx slice merges into the cumulative <see cref="GeneratedBlockAccessList"/>.
    /// Pushes the slice's rows into <see cref="_generatedValidationIndex"/> so the next
    /// <see cref="ValidateBlockAccessList"/> call at this index can take the fast path, rolls
    /// the chargeable-storage-reads counter forward, and latches the read-only-mismatch flag
    /// if any account in the slice is missing from the suggested BAL (and isn't a tolerated
    /// read-only entry).
    /// </summary>
    private void RegisterGeneratedSlice(BlockAccessListAtIndex slice)
    {
        if (_generatedValidationIndex is null)
        {
            return;
        }

        _generatedValidationIndex.Add(slice);
        foreach (AccountChangesAtIndex ac in slice.AccountChanges)
        {
            if (!IsSystemContract(ac.Address))
            {
                _generatedChargeableStorageReads += ac.StorageReads.Count;
            }
        }

        if (_suggestedValidationIndex is not null && !_hasGeneratedRequiredReadAccountMismatch)
        {
            _hasGeneratedRequiredReadAccountMismatch = HasRequiredReadAccountMissing(slice, _suggestedValidationIndex);
        }

        _hasGeneratedValidationIndexUpdates = true;
    }

    /// <summary>True iff any account in <paramref name="slice"/> has no state changes, isn't a
    /// tolerated read-only entry (system-user at index 0 or any storage-read row), and isn't
    /// declared in <paramref name="suggestedValidationIndex"/>. Such an account is invisible to
    /// the column-index fast path (no lane rows land for it on either side) but must still be
    /// rejected — <see cref="ValidateBlockAccessList"/>'s fallback walk catches it.</summary>
    private static bool HasRequiredReadAccountMissing(BlockAccessListAtIndex slice, BlockAccessListValidationIndex suggestedValidationIndex)
    {
        foreach (AccountChangesAtIndex ac in slice.AccountChanges)
        {
            if (HasStateChanges(ac)) continue;
            if (IsSystemUserReadAt0(ac, slice.Index) || ac.StorageReads.Count > 0) continue;
            if (!suggestedValidationIndex.HasAccount(ac.Address)) return true;
        }
        return false;
    }

    private static bool HasStateChanges(AccountChangesAtIndex ac)
        => ac.BalanceChange is not null
        || ac.NonceChange is not null
        || ac.CodeChange is not null
        || ac.StorageChangeCount > 0;

    private static bool IsSystemUserReadAt0(AccountChangesAtIndex ac, uint index)
        => index == 0 && ac.Address == Address.SystemUser && !HasStateChanges(ac) && ac.StorageReads.Count == 0;

    private static bool IsSystemContract(Address address)
        => address == Eip7002Constants.WithdrawalRequestPredeployAddress
        || address == Eip7251Constants.ConsolidationRequestPredeployAddress;
}
