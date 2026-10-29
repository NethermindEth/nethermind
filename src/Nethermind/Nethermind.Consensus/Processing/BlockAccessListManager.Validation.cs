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
using Nethermind.Core.Messages;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using static Nethermind.Consensus.Processing.BlockProcessor;
using static Nethermind.State.BlockAccessListBasedWorldState;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Validation paths:
///   * <see cref="IncrementalValidation"/> awaits each worker's gas result and runs the
///     EIP-8037 2D inclusion check on the cumulative gas.
///   * <see cref="CheckPerTxInclusion"/> is the EIP-8037 worst-case 2D inclusion check.
///   * <see cref="ValidateLogs"/> checks the logs against the suggested list once the block has
///     executed in parallel, merging them only to report a mismatch.
///   * <see cref="ValidateMergedBlockAccessList"/> compares the merged list with the suggested
///     one once the block has executed.
/// </summary>
public partial class BlockAccessListManager
{
    public void IncrementalValidation(Block block, GasValidationResultSlot[] gasResults, BlockReceiptsTracer[] receiptsTracers, BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessedEventHandler, CancellationToken token)
    {
        CheckInitialized();

        int len = block.Transactions.Length;

        // balIndex 0 (pre-execution) is held by main-thread system contract handlers; release its processor to the workers.
        ReturnTxProcessor(0u);

        ulong totalExecutionGas = 0;
        ulong totalStateGas = 0;
        ulong totalReceiptGas = 0;
        for (int chunkStart = 0; chunkStart < len; chunkStart += GasValidationChunkSize)
        {
            // Throws rather than returns: an early return would read as a validated block.
            token.ThrowIfCancellationRequested();

            int chunkEnd = Math.Min(chunkStart + GasValidationChunkSize, len);
            for (int j = chunkStart; j < chunkEnd; j++)
            {
                Transaction tx = block.Transactions[j];

                GasValidationResult gasResult = gasResults[j].GetResult();
                CheckPerTxInclusion(block, j, tx, _blockExecutionContext.Value.Spec, totalExecutionGas, totalStateGas);

                // Surface the worker's original tx-rejection reason before running any
                // downstream gas accounting. Otherwise CheckGasUsed can mask the true cause,
                // unlike the sequential path, which never reaches accounting on a rejected tx.
                if (gasResult.Exception is not null)
                    throw new ParallelExecutionException(gasResult.Exception);

                totalExecutionGas += gasResult.BlockGasUsed;
                totalStateGas += gasResult.BlockStateGasUsed;
                SpendGas(gasResult.BlockGasUsed);

                ulong headerGasUsed = EthereumGasPolicy.CombineBlockGas(totalExecutionGas, totalStateGas);
                CheckGasUsed(j, block, headerGasUsed);

                if (transactionProcessedEventHandler is not null)
                {
                    TxReceipt receipt = receiptsTracers[j].TxReceipts[0];
                    // IncrementalValidation also supports direct handlers, which observe the receipt
                    // before the executor combines and canonicalizes the per-transaction tracers.
                    receipt.Index = j;
                    totalReceiptGas += receipt.GasUsed;
                    receipt.GasUsedTotal = totalReceiptGas;
                    transactionProcessedEventHandler.OnTransactionProcessed(
                        new TxProcessedEventArgs(j, tx, block.Header, receipt) { HeaderGasUsed = headerGasUsed });
                }
            }
        }

        // EIP-8037: 2D gas accounting — block gasUsed = max(sum_execution, sum_state)
        _blockExecutionContext.Value.Header.GasUsed = EthereumGasPolicy.CombineBlockGas(totalExecutionGas, totalStateGas);
        _blockExecutionContext.Value.Header.GasUsedPerDimension = (totalExecutionGas, totalStateGas);

        static void CheckGasUsed(int index, Block block, ulong effectiveGas)
        {
            if (effectiveGas > block.Header.GasLimit)
            {
                throw new InvalidBlockException(block, $"Block gas limit exceeded: cumulative gas {effectiveGas} > block gas limit {block.Header.GasLimit} after transaction index {index}.");
            }
        }
    }

    // EIP-8037 worst-case 2D inclusion check. Only fires when EIP-8037 is active; legacy and
    // pre-EIP-8037 blocks rely solely on the post-execution running max(R,S) check.
    internal static void CheckPerTxInclusion(Block block, int index, Transaction tx, IReleaseSpec spec, ulong cumulativeExecution, ulong cumulativeState)
    {
        if (!spec.IsEip8037Enabled) return;

        Eip8037BlockGasInclusionCheck.Outcome outcome;
        if (Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(tx, spec, out ulong executionReservation, out ulong stateReservation))
        {
            outcome = Eip8037BlockGasInclusionCheck.Validate(
                block.Header.GasLimit,
                cumulativeExecution,
                cumulativeState,
                executionReservation,
                stateReservation);
        }
        else
        {
            // Defensive: a decoded block carries its frames, so an unpriceable transaction cannot reach
            // here. The scalar limit sums both dimensions, so were it reached it would only over-reject.
            outcome = Eip8037BlockGasInclusionCheck.Validate(
                block.Header.GasLimit,
                cumulativeExecution,
                cumulativeState,
                tx.GasLimit);
        }

        if (outcome != Eip8037BlockGasInclusionCheck.Outcome.Ok)
        {
            throw new InvalidBlockException(block,
                $"Block gas limit exceeded: tx {index} fails EIP-8037 inclusion check ({outcome}); " +
                $"execution_available={block.Header.GasLimit - cumulativeExecution}, " +
                $"state_available={block.Header.GasLimit - cumulativeState}, " +
                $"tx.gas={tx.GasLimit}.");
        }
    }

    /// <summary>Checks the logs against the block's suggested list without merging them.</summary>
    /// <remarks>
    /// Each index is recorded in one log, so the logs match when the suggested list declares all that each holds,
    /// together they hold as many changes as it and touch all its accounts, and every declared read executed. A
    /// mismatch is reported as <see cref="ValidateMergedBlockAccessList"/> reports it, over the merged logs and the
    /// declared reads that executed.
    /// </remarks>
    private void ValidateLogs(Block block, ReadOnlySpan<BlockAccessListLog> logs)
    {
        if (BlockAccessListLog.Matches(logs, block.BlockAccessList!) && _readPlan?.TryFindUncovered(out _) != true) return;

        BlockAccessIndexRecorder coveredReads = new();
        coveredReads.Begin((uint)block.Transactions.Length + 1);
        _readPlan?.RecordCoveredReads(coveredReads);
        coveredReads.Log.Seal();
        _mergedBlockAccessList.Merge([.. logs, coveredReads.Log]);
        ValidateMergedBlockAccessList(block);
    }

    /// <summary>Checks the merged list against the block's suggested one.</summary>
    /// <remarks>
    /// Equal lists need no more than the walk in <see cref="MergedBlockAccessList.Matches"/>. A mismatch is reported as
    /// a change at an index past the post-execution one; failing that, as the first block access index whose changes
    /// differ; failing that, as surplus storage reads the block's remaining gas cannot pay for; failing that, as the
    /// first structural difference.
    /// </remarks>
    private void ValidateMergedBlockAccessList(Block block)
    {
        ReadOnlyBlockAccessList suggested = block.BlockAccessList!;
        if (_mergedBlockAccessList.Matches(suggested, out _)) return;

        ThrowIfIndexOutOfRange(block, suggested);
        ThrowIfChangesDiffer(block, suggested);
        ThrowIfStorageReadBudgetExceeded(block, suggested);
        ThrowStructuralMismatch(block, suggested);
    }

    private static void ThrowIfIndexOutOfRange(Block block, ReadOnlyBlockAccessList suggested)
    {
        uint lastIndex = (uint)block.Transactions.Length + 1;
        foreach (ReadOnlyAccountChanges account in suggested.AccountChanges)
        {
            // Changes are ordered by index, so the last one of each kind is the highest.
            Check(account.BalanceChanges);
            Check(account.NonceChanges);
            Check(account.CodeChanges);
            foreach (ReadOnlySlotChanges slot in account.StorageChanges) Check(slot.Changes);
        }

        void Check<T>(ReadOnlySpan<T> changes) where T : IIndexedChange
        {
            if (!changes.IsEmpty && changes[^1].Index > lastIndex)
                throw new InvalidBlockException(block, BlockErrorMessages.BlockLevelAccessListIndexOutOfRange(changes[^1].Index, lastIndex));
        }
    }

    private void ThrowIfChangesDiffer(Block block, ReadOnlyBlockAccessList suggested)
    {
        MergedBlockAccessList generated = _mergedBlockAccessList;
        ReadOnlySpan<ReadOnlyAccountChanges> suggestedAccounts = suggested.AccountChanges.AsSpan();
        uint firstIndex = uint.MaxValue;
        Address? firstAddress = null;
        bool firstIsSurplus = false;

        for (int g = 0, s = 0; g < generated.AccountCount || s < suggestedAccounts.Length;)
        {
            int cmp = g == generated.AccountCount ? 1
                : s == suggestedAccounts.Length ? -1
                : generated.GetAccount(g).Address.Bytes.SequenceCompareTo(suggestedAccounts[s].Address.Bytes);

            if (cmp == 0)
            {
                MergedBlockAccessList.AccountChanges account = generated.GetAccount(g++);
                Consider(FirstDifferingChange(account, suggestedAccounts[s++]), account.Address, surplus: false);
            }
            else if (cmp < 0)
            {
                MergedBlockAccessList.AccountChanges account = generated.GetAccount(g++);
                bool hasChargeableReads = !IsSystemContract(account.Address) && account.StorageReads.Length > 0;
                // An account execution only touched is tolerated when it has chargeable reads, or is the system user at index 0.
                bool tolerated = hasChargeableReads || (account.FirstIndex == 0 && account.Address == Address.SystemUser);
                Consider(tolerated ? FirstChange(account) : account.FirstIndex, account.Address, surplus: false);
            }
            else
            {
                ReadOnlyAccountChanges account = suggestedAccounts[s++];
                Consider(FirstChange(account), account.Address, surplus: true);
            }
        }

        if (firstAddress is null) return;
        if (firstIsSurplus) ThrowSurplusChanges(block, firstAddress, firstIndex);
        if (suggested.GetAccountChanges(firstAddress) is null) ThrowMissingAccountChanges(block, firstAddress, firstIndex);
        ThrowIncorrectChanges(block, firstAddress, firstIndex);

        // At one index, accounts execution produced are reported before accounts only the suggested list has.
        void Consider(uint index, Address address, bool surplus)
        {
            if (index < firstIndex || (index == firstIndex && firstIsSurplus && !surplus))
            {
                firstIndex = index;
                firstAddress = address;
                firstIsSurplus = surplus;
            }
        }
    }

    private static uint FirstDifferingChange(MergedBlockAccessList.AccountChanges generated, ReadOnlyAccountChanges suggested)
    {
        uint index = uint.Min(FirstDifference(generated.BalanceChanges, suggested.BalanceChanges),
            uint.Min(FirstDifference(generated.NonceChanges, suggested.NonceChanges), FirstDifference(generated.CodeChanges, suggested.CodeChanges)));

        ReadOnlySpan<ReadOnlySlotChanges> suggestedSlots = suggested.StorageChanges;
        for (int g = 0, s = 0; g < generated.SlotCount || s < suggestedSlots.Length;)
        {
            int cmp = g == generated.SlotCount ? 1
                : s == suggestedSlots.Length ? -1
                : generated.GetSlotKey(g).CompareTo(suggestedSlots[s].Key);

            ReadOnlySpan<StorageChange> generatedChanges = cmp <= 0 ? generated.GetSlotChanges(g++) : default;
            ReadOnlySpan<StorageChange> suggestedChanges = cmp >= 0 ? suggestedSlots[s++].Changes : default;
            index = uint.Min(index, FirstDifference(generatedChanges, suggestedChanges));
        }

        return index;
    }

    /// <summary>The lower index of the first pair of changes that differ, or <see cref="uint.MaxValue"/> when none do.</summary>
    private static uint FirstDifference<T>(ReadOnlySpan<T> generated, ReadOnlySpan<T> suggested) where T : IIndexedChange, IEquatable<T>
    {
        int common = Math.Min(generated.Length, suggested.Length);
        for (int i = 0; i < common; i++)
        {
            if (!generated[i].Equals(suggested[i])) return uint.Min(generated[i].Index, suggested[i].Index);
        }

        return generated.Length > common ? generated[common].Index
            : suggested.Length > common ? suggested[common].Index
            : uint.MaxValue;
    }

    private static uint FirstChange(MergedBlockAccessList.AccountChanges account)
    {
        uint index = FirstDifference(account.BalanceChanges, default);
        index = uint.Min(index, FirstDifference(account.NonceChanges, default));
        index = uint.Min(index, FirstDifference(account.CodeChanges, default));
        for (int slot = 0; slot < account.SlotCount; slot++) index = uint.Min(index, account.GetSlotChanges(slot)[0].Index);
        return index;
    }

    private static uint FirstChange(ReadOnlyAccountChanges account)
    {
        uint index = FirstDifference<BalanceChange>(account.BalanceChanges, default);
        index = uint.Min(index, FirstDifference<NonceChange>(account.NonceChanges, default));
        index = uint.Min(index, FirstDifference<CodeChange>(account.CodeChanges, default));
        foreach (ReadOnlySlotChanges slot in account.StorageChanges) index = uint.Min(index, slot.Changes[0].Index);
        return index;
    }

    private static readonly Address[] SystemContracts =
    [
        Eip7002Constants.WithdrawalRequestPredeployAddress,
        Eip7251Constants.ConsolidationRequestPredeployAddress,
        Eip8282Constants.BuilderDepositRequestPredeployAddress,
        Eip8282Constants.BuilderExitRequestPredeployAddress,
    ];

    private static bool IsSystemContract(Address address)
    {
        foreach (Address systemContract in SystemContracts)
        {
            if (address == systemContract) return true;
        }

        return false;
    }

    /// <summary>
    /// Upper bound on the chargeable storage reads the post-execution request calls can add without spending block gas.
    /// </summary>
    /// <remarks>
    /// EIP-7928: an early surplus-reads rejection must not reject a valid block, and system-call reads are not paid
    /// for by block gas. Canonical request bytecode at its predeploy reads only its own storage, which the budget
    /// excludes; any other code may read up to its execution grant at the cold <c>SLOAD</c> cost.
    /// </remarks>
    private ulong PostExecutionReadAllowance(IReleaseSpec spec)
    {
        if (!spec.RequestsEnabled) return 0ul;

        ulong calls = 0ul;
        if (spec.WithdrawalRequestsEnabled && !IsCanonicalPredeploy(spec.Eip7002ContractAddress, Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002Constants.CodeHash)) calls++;
        if (spec.ConsolidationRequestsEnabled && !IsCanonicalPredeploy(spec.Eip7251ContractAddress, Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251Constants.CodeHash)) calls++;
        if (spec.BuilderRequestsEnabled)
        {
            // EIP-8282 predeploy addresses are fixed; the spec has no override for them.
            if (!HasCanonicalCode(Eip8282Constants.BuilderDepositRequestPredeployAddress, Eip8282Constants.BuilderDepositCodeHash)) calls++;
            if (!HasCanonicalCode(Eip8282Constants.BuilderExitRequestPredeployAddress, Eip8282Constants.BuilderExitCodeHash)) calls++;
        }

        return calls * (Eip8037Constants.SystemCallBaseGasLimit / GasCostOf.ColdSLoad);
    }

    private bool IsCanonicalPredeploy(Address? contract, Address predeploy, in ValueHash256 canonicalCodeHash)
        => contract == predeploy && HasCanonicalCode(predeploy, in canonicalCodeHash);

    private bool HasCanonicalCode(Address predeploy, in ValueHash256 canonicalCodeHash)
        => stateProvider.GetCodeHash(predeploy) == canonicalCodeHash;

    private void ThrowIfStorageReadBudgetExceeded(Block block, ReadOnlyBlockAccessList suggested)
    {
        ulong surplusReads = ChargeableStorageReads(suggested).SaturatingSub(ChargeableStorageReads(_mergedBlockAccessList));
        if (surplusReads > _postExecutionReadAllowance
            && _gasRemaining < (surplusReads - _postExecutionReadAllowance) * Eip7928Constants.ItemCost)
        {
            throw new InvalidBlockLevelAccessListException(block.Header, "Suggested block-level access list contained invalid storage reads.");
        }
    }

    private static ulong ChargeableStorageReads(ReadOnlyBlockAccessList blockAccessList)
    {
        ulong reads = 0;
        foreach (ReadOnlyAccountChanges account in blockAccessList.AccountChanges)
        {
            if (!IsSystemContract(account.Address)) reads += (ulong)account.StorageReads.Length;
        }

        return reads;
    }

    private static ulong ChargeableStorageReads(MergedBlockAccessList blockAccessList)
    {
        ulong reads = 0;
        for (int i = 0; i < blockAccessList.AccountCount; i++)
        {
            MergedBlockAccessList.AccountChanges account = blockAccessList.GetAccount(i);
            if (!IsSystemContract(account.Address)) reads += (ulong)account.StorageReads.Length;
        }

        return reads;
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

    /// <summary>Reports the account set or the storage_reads of an account differing from the suggested list.</summary>
    [DoesNotReturn, StackTraceHidden]
    private void ThrowStructuralMismatch(Block block, ReadOnlyBlockAccessList suggested)
    {
        MergedBlockAccessList generated = _mergedBlockAccessList;
        if (generated.AccountCount != suggested.AccountChanges.Count)
        {
            ThrowMismatch(block, $"Account-set size mismatch: suggested={suggested.AccountChanges.Count}, generated={generated.AccountCount}.");
        }

        // Equal counts: the first suggested account that is not at its place was not touched.
        ReadOnlySpan<ReadOnlyAccountChanges> suggestedAccounts = suggested.AccountChanges.AsSpan();
        for (int i = 0; i < generated.AccountCount; i++)
        {
            MergedBlockAccessList.AccountChanges generatedAccount = generated.GetAccount(i);
            ReadOnlyAccountChanges suggestedAccount = suggestedAccounts[i];
            if (!generatedAccount.Address.Equals(suggestedAccount.Address))
            {
                ThrowMismatch(block, $"Suggested BAL declares account {suggestedAccount.Address} which execution did not touch.");
            }

            if (generatedAccount.StorageReads.Length != suggestedAccount.StorageReads.Length)
            {
                ThrowMismatch(block, $"storage_reads count mismatch for {suggestedAccount.Address}.");
            }

            if (!generatedAccount.StorageReads.SequenceEqual(suggestedAccount.StorageReads))
            {
                ThrowMismatch(block, $"storage_reads mismatch for {suggestedAccount.Address}.");
            }
        }

        ThrowMismatch(block, "Suggested block-level access list does not match the generated one.");
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowMismatch(Block block, string error)
        => throw new InvalidBlockLevelAccessListException(block.Header, error);
}
