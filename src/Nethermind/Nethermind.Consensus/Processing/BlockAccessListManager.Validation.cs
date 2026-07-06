// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
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

        ulong totalRegularGas = 0;
        ulong totalStateGas = 0;
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
                CheckPerTxInclusion(block, j, tx, _blockExecutionContext.Value.Spec, totalRegularGas, totalStateGas);

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
        _blockExecutionContext.Value.Header.GasUsed = EthereumGasPolicy.CombineBlockGas(totalRegularGas, totalStateGas);

        static void CheckGasUsed(int index, Block block, ulong totalRegularGas, ulong totalStateGas)
        {
            // EIP-8037: block gasUsed = max(sum_regular, sum_state)
            ulong effectiveGas = EthereumGasPolicy.CombineBlockGas(totalRegularGas, totalStateGas);
            if (effectiveGas > block.Header.GasLimit)
            {
                throw new InvalidBlockException(block, $"Block gas limit exceeded: cumulative gas {effectiveGas} > block gas limit {block.Header.GasLimit} after transaction index {index}.");
            }
        }
    }

    // EIP-8037 worst-case 2D inclusion check. Only fires when EIP-8037 is active; legacy and
    // pre-EIP-8037 blocks rely solely on the post-execution running max(R,S) check.
    internal static void CheckPerTxInclusion(Block block, int index, Transaction tx, IReleaseSpec spec, ulong cumulativeRegular, ulong cumulativeState)
    {
        if (!spec.IsEip8037Enabled) return;

        Eip8037BlockGasInclusionCheck.Outcome outcome = Eip8037BlockGasInclusionCheck.Validate(
            block.Header.GasLimit,
            cumulativeRegular,
            cumulativeState,
            tx.GasLimit);

        if (outcome != Eip8037BlockGasInclusionCheck.Outcome.Ok)
        {
            throw new InvalidBlockException(block,
                $"Block gas limit exceeded: tx {index} fails EIP-8037 inclusion check ({outcome}); " +
                $"regular_available={block.Header.GasLimit - cumulativeRegular}, " +
                $"state_available={block.Header.GasLimit - cumulativeState}, " +
                $"tx.gas={tx.GasLimit}.");
        }
    }

    public void ValidateBlockAccessList(Block block, uint index, bool validateStorageReads = true)
    {
        if (block.BlockAccessList is null) return;

        CheckInitialized();

        if (TryFastPath(block, index, validateStorageReads)) return;

        if (VerifyOnly && _suggestedValidationIndex is not null && _generatedValidationIndex is not null)
        {
            SlowPathFromColumnIndex(block, index, validateStorageReads);
            return;
        }

        SlowPathFromGeneratedBlockAccessList(block, index, validateStorageReads);
    }

    /// <summary>
    /// Column-index per-row equality with a final surplus-reads gas budget check. Skipped when
    /// the index hasn't received any updates, when a per-tx slice already surfaced a generated-
    /// only account invisible to <see cref="BlockAccessListValidationIndex.ChangesEqual"/>, or
    /// when ChangesEqual itself detects a row-level mismatch.
    /// </summary>
    private bool TryFastPath(Block block, uint index, bool validateStorageReads)
    {
        if (!_hasGeneratedValidationIndexUpdates ||
            _suggestedValidationIndex is null ||
            _generatedValidationIndex is null ||
            _hasGeneratedRequiredReadAccountMismatch ||
            !_generatedValidationIndex.ChangesEqual(_suggestedValidationIndex, index))
        {
            return false;
        }

        ulong surplusReads = _suggestedChargeableStorageReads.SaturatingSub(_generatedChargeableStorageReads);
        ThrowIfStorageReadBudgetExceeded(block, surplusReads, validateStorageReads);
        return true;
    }

    /// <summary>
    /// Non-verify-only diagnostic walk: the constructed <see cref="GeneratedBlockAccessList"/>
    /// drives a two-pass comparison against the suggested BAL. Used by the BAL recorder and the
    /// sequential build where the constructed list is populated anyway.
    /// </summary>
    private void SlowPathFromGeneratedBlockAccessList(Block block, uint index, bool validateStorageReads)
    {
        GeneratedBlockAccessList generated = GeneratedBlockAccessList;
        ReadOnlyBlockAccessList suggested = block.BlockAccessList!;

        ulong generatedReads = 0;
        ulong suggestedReads = 0;

        // Pass 1: every account generated touched must match suggested at this index (O(1)
        // dictionary lookup) or be a tolerated generated-only entry.
        foreach (GeneratedAccountChanges gen in generated.AccountChanges)
        {
            int genReads = IsSystemContract(gen.Address) ? 0 : gen.StorageReads.Count;
            generatedReads += (ulong)genReads;

            ReadOnlyAccountChanges? sug = suggested.GetAccountChanges(gen.Address);
            if (sug is not null)
            {
                if (!gen.ChangesAtIndexEqual(sug, index))
                {
                    ThrowIncorrectChanges(block, gen.Address, index);
                }
                continue;
            }

            if (IsToleratedGeneratedOnlyAccount(gen.Address, index, gen.HasNoChangesAtIndex(index), hasChargeableReads: genReads > 0)) continue;

            ThrowMissingAccountChanges(block, gen.Address, index);
        }

        // Pass 2: accounts only in suggested must carry no changes at this index (else surplus).
        // Tally suggested reads here for the storage-read gas-budget check below.
        foreach (ReadOnlyAccountChanges sug in suggested.AccountChanges)
        {
            suggestedReads += IsSystemContract(sug.Address) ? 0ul : (ulong)sug.StorageReads.Length;

            if (generated.HasAccount(sug.Address)) continue;

            if (!sug.HasNoChangesAtIndex(index)) ThrowSurplusChanges(block, sug.Address, index);
        }

        ulong surplusReads = suggestedReads.SaturatingSub(generatedReads);
        ThrowIfStorageReadBudgetExceeded(block, surplusReads, validateStorageReads);
    }

    /// <summary>
    /// Verify-only slow path: produces per-account, per-row diagnostics by walking only the
    /// column-index data structures. Required when per-tx Merge is skipped so generated is empty.
    /// </summary>
    private void SlowPathFromColumnIndex(Block block, uint index, bool validateStorageReads)
    {
        BlockAccessListValidationIndex gen = _generatedValidationIndex!;
        BlockAccessListValidationIndex sug = _suggestedValidationIndex!;
        int row = (int)index;

        // Row capacity tracks suggested, so an overflow signals generated produced a change at
        // a (row, lane) suggested doesn't declare — HasAt would otherwise hide the dropped entry.
        if (gen.TryGetGeneratedOverflow(out Address overflowAddress, out uint overflowIndex) && overflowIndex <= index)
        {
            ThrowIncorrectChanges(block, overflowAddress, overflowIndex);
        }

        // Pass 1: every account generated touched must match suggested at this row (lane compare)
        // or be a tolerated generated-only entry.
        foreach (int ordinal in gen.EnumerateMarkedOrdinals())
        {
            Address address = gen.AddressOf(ordinal);

            if (sug.HasAccount(ordinal))
            {
                if (!gen.Lanes.ChangesAtRowEqualForOrdinal(sug.Lanes, row, ordinal)) ThrowIncorrectChanges(block, address, index);
                continue;
            }

            bool hasChargeableReads = !IsSystemContract(address) && gen.HasStorageReadsForOrdinal(ordinal);
            if (IsToleratedGeneratedOnlyAccount(address, index, hasNoChangesAtIndex: !gen.Lanes.HasAt(row, ordinal), hasChargeableReads)) continue;

            ThrowMissingAccountChanges(block, address, index);
        }

        // Pass 2: accounts only in suggested must carry no changes at this row (else surplus).
        foreach (int ordinal in sug.EnumerateMarkedOrdinals())
        {
            if (gen.HasAccount(ordinal)) continue; // already handled in Pass 1
            if (sug.Lanes.HasAt(row, ordinal)) ThrowSurplusChanges(block, sug.AddressOf(ordinal), index);
        }

        // Storage-read gas budget — counts already tracked block-cumulative on both sides.
        ulong surplusReads = _suggestedChargeableStorageReads.SaturatingSub(_generatedChargeableStorageReads);
        ThrowIfStorageReadBudgetExceeded(block, surplusReads, validateStorageReads);
    }

    /// <summary>
    /// Hook called by <see cref="ITxProcessorWithWorldStateManager.MergeAndReturnBal"/> after each
    /// per-tx slice merges into the cumulative <see cref="GeneratedBlockAccessList"/>.
    /// </summary>
    /// <remarks>
    /// Pushes the slice's rows into <see cref="_generatedValidationIndex"/> so the next
    /// <see cref="ValidateBlockAccessList"/> call at this index can take the fast path, rolls the
    /// chargeable-storage-reads counter forward, and latches the read-only-mismatch flag if any
    /// account in the slice is missing from the suggested BAL (and isn't a tolerated read-only
    /// entry).
    /// </remarks>
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
                _generatedChargeableStorageReads += (ulong)ac.StorageReads.Count;
            }
        }

        if (_suggestedValidationIndex is not null && !_hasGeneratedRequiredReadAccountMismatch)
        {
            _hasGeneratedRequiredReadAccountMismatch = HasRequiredReadAccountMissing(slice, _suggestedValidationIndex);
        }

        _hasGeneratedValidationIndexUpdates = true;
    }

    /// <summary>
    /// True iff any account in <paramref name="slice"/> has no state changes, isn't a tolerated
    /// read-only entry (system-user at index 0 or any storage-read row), and isn't declared in
    /// <paramref name="suggestedValidationIndex"/>. Such an account is invisible to the
    /// column-index fast path (no lane rows land for it on either side) but must still be
    /// rejected — <see cref="ValidateBlockAccessList"/>'s fallback walk catches it.
    /// </summary>
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

    private static bool IsToleratedGeneratedOnlyAccount(Address address, uint index, bool hasNoChangesAtIndex, bool hasChargeableReads)
        => hasNoChangesAtIndex
        && ((index == 0 && address == Address.SystemUser && !hasChargeableReads) || hasChargeableReads);

    private void ThrowIfStorageReadBudgetExceeded(Block block, ulong surplusReads, bool validateStorageReads)
    {
        if (validateStorageReads && surplusReads > 0ul && _gasRemaining < surplusReads * Eip7928Constants.ItemCost)
        {
            throw new InvalidBlockLevelAccessListException(block.Header, "Suggested block-level access list contained invalid storage reads.");
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowIncorrectChanges(Block block, Address address, uint index)
        => throw new InvalidBlockLevelAccessListException(block.Header, $"Suggested block-level access list contained incorrect changes for {address} at index {index}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMissingAccountChanges(Block block, Address address, uint index)
        => throw new InvalidBlockLevelAccessListException(block.Header, $"Suggested block-level access list missing account changes for {address} at index {index}.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowSurplusChanges(Block block, Address address, uint index)
        => throw new InvalidBlockLevelAccessListException(block.Header, $"Suggested block-level access list contained surplus changes for {address} at index {index}.");

    /// <summary>
    /// Closes the gap between the column-index per-row validation and what the end-of-block
    /// canonical-bytes hash compare used to catch: namely, account-set presence and the exact
    /// set of storage_reads per account. Throws <see cref="InvalidBlockLevelAccessListException"/>
    /// on any structural difference between the suggested and generated BALs.
    /// </summary>
    /// <remarks>
    /// Does not compare <c>StorageChanges</c> (write entries): those are fully covered by the
    /// incremental <see cref="ValidateBlockAccessList"/> calls at indices 0..txCount+1 via
    /// <c>ChangesAtIndexEqual</c>, which compares every balance/nonce/code/storage-write lane at
    /// each tx index in lockstep.
    /// </remarks>
    private void ValidateStructuralEquivalence(Block block)
    {
        BlockAccessListValidationIndex generatedIndex = _generatedValidationIndex!;
        ReadOnlyBlockAccessList suggested = block.BlockAccessList!;

        // Generated lane Add dropped a row that didn't fit suggested's per-row capacity —
        // structural mismatch the per-account walk below can't see through HasAt.
        if (generatedIndex.TryGetGeneratedOverflow(out Address overflowAddress, out uint overflowIndex))
        {
            ThrowIncorrectChanges(block, overflowAddress, overflowIndex);
        }

        BlockAccessListValidationIndex.StructuralMismatchKind mismatch =
            generatedIndex.FindStructuralMismatch(suggested, out Address? mismatchAddress, out int generatedAccountCount);

        string? error = mismatch switch
        {
            BlockAccessListValidationIndex.StructuralMismatchKind.None => null,
            BlockAccessListValidationIndex.StructuralMismatchKind.AccountCountMismatch
                => $"Account-set size mismatch: suggested={suggested.AccountChanges.Count}, generated={generatedAccountCount}.",
            BlockAccessListValidationIndex.StructuralMismatchKind.MissingInGenerated
                => $"Suggested BAL declares account {mismatchAddress} which execution did not touch.",
            BlockAccessListValidationIndex.StructuralMismatchKind.StorageReadsCountMismatch
                => $"storage_reads count mismatch for {mismatchAddress}.",
            BlockAccessListValidationIndex.StructuralMismatchKind.StorageReadsContentMismatch
                => $"storage_reads mismatch for {mismatchAddress}.",
            _ => throw new InvalidOperationException($"Unhandled {nameof(BlockAccessListValidationIndex.StructuralMismatchKind)}: {mismatch}"),
        };

        if (error is not null) throw new InvalidBlockLevelAccessListException(block.Header, error);
    }
}
