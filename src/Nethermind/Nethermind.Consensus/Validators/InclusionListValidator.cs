// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public static class InclusionListValidator
{
    private const int StackAllocEntries = 256;

    /// <summary>EIP-7805 <c>is_profile_2_candidate</c> condition 1: a frame transaction's noncryptographic static
    /// checks, as <see cref="TxValidator"/> registers them for frame transactions less the chain ID, which the
    /// replay checks instead, so a wrong chain ID still costs its list's budget.</summary>
    internal static ITxValidator Profile2StaticChecks { get; } = new CompositeTxValidator(
        new ReleaseSpecTxValidator(static spec => spec.IsEip8141Enabled),
        NonceCapTxValidator.Instance,
        GasFieldsTxValidator.Instance,
        FrameTxFieldsTxValidator.Instance,
        FrameTxNonceKeysTxValidator.Instance);

    /// <summary>Whether <paramref name="block"/> honours EIP-7805's separate inclusion lists under the builder's
    /// omission claims.</summary>
    /// <remarks>Checks appendability against state after transactions, before withdrawals and request system calls.</remarks>
    /// <param name="inclusionLists">One decoded list per committee position, each in its original order with its
    /// duplicates; the per-IL VERIFY budget is filled over each independently.</param>
    /// <param name="maxVerifyGasPerTx">EIP-7805 <c>MAX_VERIFY_GAS_PER_TX</c>, above which a frame transaction is
    /// no Profile 2 candidate and its omission is excused; <c>0</c> lifts the cap.</param>
    /// <param name="claims">The builder's committed omission claims, in order and with their duplicates.</param>
    /// <param name="replayer">Decides Profile 2 eligibility; <c>null</c> enforces every omitted candidate that
    /// passes the determinate checks.</param>
    /// <param name="gasUsedBefore">EIP-8037 execution and state gas the block used before each transaction index,
    /// one entry per transaction, which a Profile 2 candidate claimed before the end of the payload must fit;
    /// <c>null</c> judges that fit against the whole block's gas, which only excuses more.</param>
    public static bool IsSatisfied(Block block, IReadOnlyList<Transaction[]> inclusionLists, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator,
        ulong maxVerifyGasPerTx, OmissionClaim[]? claims, IProfile2EligibilityReplayer? replayer = null, IReadOnlyList<(ulong Execution, ulong State)>? gasUsedBefore = null)
    {
        if (!spec.InclusionListsEnabled) return true;
        Transaction[] il = InclusionLists.Distinct(inclusionLists);

        // EIP-7805: before Profile 2 is active, committee positions and claims are carried but have no effect.
        bool profile2Active = Eip8369Profile2.IsActive(spec);
        if (!profile2Active) claims = null;

        // No room for even the cheapest possible tx → nothing is appendable, unless a claim moves an
        // evaluation before the end of the payload.
        ulong minIntrinsicGas = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        if (claims is not { Length: > 0 } && block.GasUsed + minIntrinsicGas > block.GasLimit) return true;

        // A conforming aggregate runs to tens of thousands of entries, far past what the stack can hold.
        bool[]? rented = il.Length > StackAllocEntries
            ? ArrayPool<bool>.Shared.Rent(il.Length)
            : null;
        try
        {
            Span<bool> included = rented is null ? stackalloc bool[il.Length] : rented.AsSpan(0, il.Length);
            included.Clear();
            if (gasUsedBefore is not null && gasUsedBefore.Count != block.Transactions.Length) gasUsedBefore = null;
            Profile2Inputs profile2 = new(profile2Active, maxVerifyGasPerTx, inclusionLists,
                ClaimedIndices(claims, block.Transactions.Length), replayer, gasUsedBefore);
            return IsSatisfied(block, il, included, state, spec, txValidator, in profile2);
        }
        finally
        {
            if (rented is not null) ArrayPool<bool>.Shared.Return(rented);
        }
    }

    private static bool IsSatisfied(Block block, Transaction[] il, Span<bool> included, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, in Profile2Inputs profile2)
    {
        // Index the first copy of each hash; included copies satisfy every occurrence in the IL.
        Dictionary<Hash256, int> ilByHash = new(il.Length);
        for (int i = 0; i < il.Length; i++)
        {
            Hash256? h = il[i].Hash;
            if (h is not null) ilByHash.TryAdd(h, i);
        }

        foreach (Transaction blockTx in block.Transactions)
        {
            if (blockTx.Hash is not null && ilByHash.TryGetValue(blockTx.Hash, out int idx))
                included[idx] = true;
        }

        Dictionary<AddressAsKey, AccountStruct>? accountCache = null;
        HashSet<Hash256AsKey>? admitted = null;
        List<(Transaction Transaction, int Index)>? toReplay = null;
        for (int i = 0; i < il.Length; i++)
        {
            if (included[i]) continue;
            if (il[i].Hash is { } hash && ilByHash.TryGetValue(hash, out int first) && included[first]) continue;
            if (il[i].SupportsFrames && profile2.Active)
            {
                // EIP-8369: a frame transaction outside Profile 2 is enforced by no profile, so its omission is excused.
                if (Eip8369Profile2.Classify(il[i], profile2.MaxVerifyGasPerTx) != Profile2Exclusion.None) continue;
                // Eligibility is a property of the bytes, so the first occurrence answers for every copy.
                if (il[i].Hash is not { } frameHash || ilByHash[frameHash] != i) continue;
                // EIP-7805 § Evaluation index: the builder's claim, if it is the hash's only one, else the end of the payload.
                int index = profile2.ClaimedIndices?.TryGetValue(frameHash, out int? claimed) == true && claimed is { } at ? at : block.Transactions.Length;
                if (!IsReplayableOmission(il[i], index, block, spec, txValidator, in profile2, ref admitted)) continue;
                if (profile2.Replayer is null) return false;
                (toReplay ??= []).Add((il[i], index));
                continue;
            }
            // Before Profile 2 is active a frame transaction is excused, as the end-of-payload rule cannot judge its
            // programmable validation.
            if (il[i].SupportsFrames) continue;
            // EIP-7805 excludes blob transactions from the appendability check.
            if (il[i].SupportsBlobs) continue;
            if (CouldIncludeTx(il[i], block, state, spec, txValidator, ref accountCache)) return false;
        }
        return toReplay is null || !AnyUnjustified(block, toReplay, spec, in profile2);
    }

    /// <summary>The inputs EIP-8369 Profile 2 enforcement takes beyond the list itself.</summary>
    private readonly record struct Profile2Inputs(bool Active, ulong MaxVerifyGasPerTx, IReadOnlyList<Transaction[]> InclusionLists,
        Dictionary<Hash256AsKey, int?>? ClaimedIndices, IProfile2EligibilityReplayer? Replayer, IReadOnlyList<(ulong Execution, ulong State)>? GasUsedBefore);

    /// <summary>Whether an omitted Profile 2 candidate passes every check but the replay at its evaluation index:
    /// the static, fee and gas-fit conditions, and admission by some committee position's VERIFY budget fill.</summary>
    /// <remarks>Only these reach the replay, so replay work is bounded by the per-IL budgets. The payer's balance is
    /// left to the replay, which collects the maximum cost from the state at <paramref name="index"/>.</remarks>
    private static bool IsReplayableOmission(
        Transaction tx,
        int index,
        Block block,
        IReleaseSpec spec,
        ITxValidator txValidator,
        in Profile2Inputs profile2,
        ref HashSet<Hash256AsKey>? admitted)
    {
        if (tx.Hash is null || tx.SenderAddress is null) return false;
        if (!FitsRemainingBlockGas(tx, block, spec, GasUsedBefore(block, index, profile2.GasUsedBefore))) return false;
        if (!txValidator.IsWellFormed(tx, spec, block.GasLimit)) return false;
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;
        if (!FrameTxValidation.TryCalculateGasBudget(tx, spec, out _, out _, out _) || !FrameTxValidation.TryCalculateMaxCost(tx, spec, out _)) return false;

        admitted ??= Eip8369Profile2.AdmitByVerifyBudget(InclusionLists.InFillOrder(profile2.InclusionLists), profile2.MaxVerifyGasPerTx,
            profile2.Replayer is { } signatures ? occurrence => signatures.AreSignaturesValid(occurrence, spec) : null,
            entry => Profile2StaticChecks.IsWellFormed(entry, spec));
        return admitted.Contains(tx.Hash);
    }

    /// <summary>EIP-7805 § VERIFY budget: whether omitting any of <paramref name="candidates"/>, each admitted by some position, is unjustified.</summary>
    /// <remarks>
    /// Each is replayed once, at its evaluation index: EIP-7805 judges a claimed candidate at its claim alone, so a
    /// claim at an index where the candidate is valid condemns the omission even if it is invalid at the end. Every
    /// replay goes to the replayer in one batch, so it reconstructs the block's state once.
    /// </remarks>
    private static bool AnyUnjustified(Block block, List<(Transaction Transaction, int Index)> candidates, IReleaseSpec spec, in Profile2Inputs profile2)
    {
        foreach (bool eligible in profile2.Replayer!.AreEligible(block, candidates, spec))
        {
            if (eligible) return true;
        }
        return false;
    }

    /// <summary>The index each claimed hash commits to, built once per check; <c>null</c> for a hash claimed more than once.</summary>
    /// <remarks>EIP-7805 § Evaluation index: an index past the end of the payload is clamped to it. A hash claimed
    /// more than once has no single commitment, so every claim for it is ignored.</remarks>
    private static Dictionary<Hash256AsKey, int?>? ClaimedIndices(OmissionClaim[]? claims, int transactionCount)
    {
        if (claims is not { Length: > 0 }) return null;

        Dictionary<Hash256AsKey, int?> indices = new(claims.Length);
        foreach (OmissionClaim claim in claims)
        {
            if (claim.TransactionHash is not { } hash) continue;
            ref int? index = ref CollectionsMarshal.GetValueRefOrAddDefault(indices, hash, out bool exists);
            index = exists ? null : claim.EvaluationIndex < (ulong)transactionCount ? (int)claim.EvaluationIndex : transactionCount;
        }
        return indices;
    }

    /// <summary>Whether an omitted non-frame inclusion-list entry could have been appended to <paramref name="block"/>,
    /// judged at the end of the payload.</summary>
    private static bool CouldIncludeTx(Transaction tx, Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ref Dictionary<AddressAsKey, AccountStruct>? accountCache)
    {
        if (tx.SenderAddress is null) return false;
        if (!FitsRemainingBlockGas(tx, block, spec, GasUsedBefore(block, block.Transactions.Length, null))) return false;
        // Appendability must match normal execution, so reuse the full well-formedness check, not a subset.
        if (!txValidator.IsWellFormed(tx, spec, block.GasLimit)) return false;
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;

        Address payer = tx.SenderAddress;
        accountCache ??= [];
        ref AccountStruct account = ref CollectionsMarshal.GetValueRefOrAddDefault(accountCache, payer, out bool cached);
        // Cache the negative result too (default struct = balance 0, nonce 0, empty codehash).
        if (!cached) state.TryGetAccount(payer, out account);

        // EIP-3607: a sender with non-delegated code cannot send a tx.
        if (account.HasCode && !state.IsDelegatedCode(account.CodeHash)) return false;

        // Overflow-checked like TransactionProcessor.BuyGas: an adversarial MaxFeePerGas must not wrap the cost.
        if (UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 txCost)
            || UInt256.AddOverflow(txCost, tx.Value, out txCost))
            return false;

        return account.Balance >= txCost && account.Nonce == tx.Nonce;
    }

    /// <summary>Whether an appended transaction's worst-case block gas still fits.</summary>
    /// <remarks>EIP-8037 admits a transaction only if both its execution and its state reservation fit the
    /// matching dimension, so measuring it against the header's max(execution, state) rejects transactions the
    /// spec judges includable. Callers whose block carries no dimensions must not put an EIP-8037 block to this
    /// check — the max alone under-reports censorship — which is why the engine API declines to answer instead.</remarks>
    private static bool FitsRemainingBlockGas(Transaction tx, Block block, IReleaseSpec spec, (ulong Execution, ulong State) gasUsed)
    {
        (ulong execution, ulong state) = gasUsed;
        // Subtract on the block side: gas used <= GasLimit is invariant, so this cannot underflow the
        // way GasLimit - tx.GasLimit would for an oversized tx.
        if (!spec.IsEip8037Enabled)
        {
            ulong limit = tx.SupportsFrames && FrameTxValidation.TryCalculateGasBudget(tx, spec, out _, out _, out ulong maxGas) ? maxGas : tx.GasLimit;
            return execution <= block.GasLimit && limit <= block.GasLimit - execution;
        }

        return Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(tx, spec, out ulong executionReservation, out ulong stateReservation)
            && Eip8037BlockGasInclusionCheck.Validate(block.GasLimit, execution, state, executionReservation, stateReservation)
                == Eip8037BlockGasInclusionCheck.Outcome.Ok;
    }

    /// <summary>The block's gas used before transaction <paramref name="index"/>, in each EIP-8037 dimension.</summary>
    /// <remarks>Without per-transaction totals an index before the end is charged the whole block's gas, which only
    /// excuses more; callers that need the exact answer supply them or bound it.</remarks>
    private static (ulong Execution, ulong State) GasUsedBefore(Block block, int index, IReadOnlyList<(ulong Execution, ulong State)>? gasUsedBefore) =>
        index < block.Transactions.Length && gasUsedBefore is not null
            ? gasUsedBefore[index]
            : block.Header.GasUsedPerDimension ?? (block.GasUsed, block.GasUsed);

    /// <summary>The EIP-8037 gas the block used before each of its transactions, from the receipts its execution produced.</summary>
    /// <returns><c>null</c> unless the receipts' per-transaction gas adds up to the block's recorded totals.</returns>
    public static (ulong Execution, ulong State)[]? GasUsedBeforeEachTransaction(Block block, IReadOnlyList<TxReceipt> receipts)
    {
        if (receipts.Count != block.Transactions.Length || block.Header.GasUsedPerDimension is not { } total) return null;

        (ulong Execution, ulong State)[] before = new (ulong, ulong)[receipts.Count];
        ulong execution = 0, state = 0;
        for (int i = 0; i < receipts.Count; i++)
        {
            before[i] = (execution, state);
            execution += receipts[i].BlockGasUsed;
            state += receipts[i].StorageGasUsed;
        }
        return (execution, state) == total ? before : null;
    }
}
