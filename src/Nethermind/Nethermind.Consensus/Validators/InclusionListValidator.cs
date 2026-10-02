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
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators;

public static class InclusionListValidator
{
    private const int StackAllocEntries = 256;

    public static bool IsSatisfied(Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ulong maxVerifyGasPerTx = Eip8369Constants.MaxVerifyGasPerTx, IProfile2EligibilityReplayer? replayer = null)
        => IsSatisfied(block, block.InclusionListTransactions, state, spec, txValidator, maxVerifyGasPerTx, block.InclusionListMembership, block.InclusionListClaims, replayer);

    /// <param name="maxVerifyGasPerTx">EIP-8369 <c>MAX_VERIFY_GAS_PER_TX</c>, above which a frame transaction is
    /// no Profile 2 candidate and its omission is excused; <c>0</c> lifts the cap.</param>
    /// <param name="ilMembership">One <see cref="InclusionListMembership"/> mask per entry of <paramref name="il"/>, which
    /// the EIP-8369 per-IL VERIFY budget is filled over; <c>null</c> admits no Profile 2 candidate.</param>
    /// <param name="claims">The builder's EIP-8369 claimed evaluation indices.</param>
    /// <param name="replayer">Decides Profile 2 eligibility; <c>null</c> enforces every omitted candidate that
    /// passes the determinate checks.</param>
    public static bool IsSatisfied(Block block, Transaction[]? il, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ulong maxVerifyGasPerTx = Eip8369Constants.MaxVerifyGasPerTx,
        ushort[]? ilMembership = null, InclusionListClaim[]? claims = null, IProfile2EligibilityReplayer? replayer = null)
    {
        if (!spec.InclusionListsEnabled) return true;
        // No IL attached = non-engine-API path (genesis, RLP import); IL doesn't apply.
        if (il is null) return true;

        // No room for even the cheapest possible tx → nothing is appendable.
        ulong minIntrinsicGas = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        if (block.GasUsed + minIntrinsicGas > block.GasLimit) return true;

        // A conforming aggregate runs to tens of thousands of entries, far past what the stack can hold.
        bool[]? rented = il.Length > StackAllocEntries
            ? ArrayPool<bool>.Shared.Rent(il.Length)
            : null;
        try
        {
            Span<bool> included = rented is null ? stackalloc bool[il.Length] : rented.AsSpan(0, il.Length);
            included.Clear();
            Profile2Inputs profile2 = new(maxVerifyGasPerTx, ilMembership?.Length == il.Length ? ilMembership : null, claims, replayer);
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
        List<Transaction>? toReplay = null;
        for (int i = 0; i < il.Length; i++)
        {
            if (included[i]) continue;
            if (il[i].Hash is { } hash && ilByHash.TryGetValue(hash, out int first) && included[first]) continue;
            if (il[i].SupportsFrames)
            {
                // EIP-8369: a frame transaction outside Profile 2 is enforced by no profile, so its omission is excused.
                if (Eip8369Profile2.Classify(il[i], profile2.MaxVerifyGasPerTx) != Profile2Exclusion.None) continue;
                // Eligibility is a property of the bytes, so the first occurrence answers for every copy.
                if (il[i].Hash is { } frameHash && ilByHash[frameHash] != i) continue;
                if (!IsReplayableOmission(il[i], block, il, state, spec, txValidator, in profile2, ref accountCache, ref admitted)) continue;
                if (profile2.Replayer is null) return false;
                (toReplay ??= []).Add(il[i]);
                continue;
            }
            if (CouldIncludeTx(il[i], block, state, spec, txValidator, ref accountCache)) return false;
        }
        return toReplay is null || !AnyUnjustified(block, toReplay, spec, in profile2);
    }

    /// <summary>The inputs EIP-8369 Profile 2 enforcement takes beyond the list itself.</summary>
    private readonly record struct Profile2Inputs(ulong MaxVerifyGasPerTx, ushort[]? IlMembership, InclusionListClaim[]? Claims, IProfile2EligibilityReplayer? Replayer);

    /// <summary>Whether an omitted Profile 2 candidate passes every check but the replay: the determinate
    /// conditions at the end of the payload and admission by some committee position's VERIFY budget fill.</summary>
    /// <remarks>Only these reach the replay, so replay work is bounded by the per-IL budgets. Without membership no
    /// position carries the entry, so it is not admitted (EIP-7805 as amended by EIP-8369).</remarks>
    private static bool IsReplayableOmission(
        Transaction tx,
        Block block,
        Transaction[] il,
        IReadOnlyStateProvider state,
        IReleaseSpec spec,
        ITxValidator txValidator,
        in Profile2Inputs profile2,
        ref Dictionary<AddressAsKey, AccountStruct>? accountCache,
        ref HashSet<Hash256AsKey>? admitted)
    {
        if (tx.Hash is null || profile2.IlMembership is not { } ilMembership) return false;
        if (!CouldIncludeTx(tx, block, state, spec, txValidator, ref accountCache)) return false;

        admitted ??= Eip8369Profile2.AdmitByVerifyBudget(InclusionListMembership.ByPosition(il, ilMembership), profile2.MaxVerifyGasPerTx,
            profile2.Replayer is { } signatures ? occurrence => signatures.AreSignaturesValid(occurrence, spec) : null);
        return admitted.Contains(tx.Hash);
    }

    /// <summary>EIP-8369 § Attesters: whether omitting any of <paramref name="candidates"/> is unjustified.</summary>
    /// <remarks>
    /// Each is judged at the default index, the end of the payload, and again at its claimed index when the
    /// builder committed one; the omission is unjustified only at both, so a claim can excuse but never condemn.
    /// Gas remaining only grows towards the start of the payload, so the fit at the end covers the claim. Every
    /// replay goes to the replayer in one batch, so it reconstructs the block's state once.
    /// </remarks>
    private static bool AnyUnjustified(Block block, List<Transaction> candidates, IReleaseSpec spec, in Profile2Inputs profile2)
    {
        int end = block.Transactions.Length;
        List<(Transaction Transaction, int Index)> requests = new(candidates.Count * 2);
        int[] claimedAt = new int[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            requests.Add((candidates[i], end));
            claimedAt[i] = -1;
            if (ResolveClaimedIndex(profile2.Claims, candidates[i].Hash!, end) is { } claimed && claimed != end)
            {
                claimedAt[i] = requests.Count;
                requests.Add((candidates[i], claimed));
            }
        }

        bool[] eligible = profile2.Replayer!.AreEligible(block, requests, spec);
        for (int i = 0, next = 0; i < candidates.Count; i++, next++)
        {
            bool unjustified = eligible[next] && (claimedAt[i] < 0 || eligible[claimedAt[i]]);
            if (claimedAt[i] >= 0) next++;
            if (unjustified) return true;
        }
        return false;
    }

    /// <summary>The index <paramref name="claims"/> commits for <paramref name="hash"/>, or <c>null</c> for none.</summary>
    /// <remarks>EIP-8369 § Builders: an out-of-range index defaults to the end of the payload. A hash claimed
    /// more than once has no single commitment, so every claim for it is ignored.</remarks>
    private static int? ResolveClaimedIndex(InclusionListClaim[]? claims, Hash256 hash, int transactionCount)
    {
        if (claims is null) return null;

        int? index = null;
        foreach (InclusionListClaim claim in claims)
        {
            if (claim.TransactionHash != hash) continue;
            if (index is not null) return null;
            index = claim.TransactionIndex < (ulong)transactionCount ? (int)claim.TransactionIndex : transactionCount;
        }
        return index;
    }

    /// <summary>Whether an omitted inclusion-list entry could have been appended to <paramref name="block"/>.</summary>
    /// <remarks>Judges an EIP-8369 Profile 2 candidate on the conditions its frame layout makes determinate —
    /// the gas it would add, fee validity, and whether the payer its prefix nominates could cover the EIP-8141
    /// maximum cost. The account nonce and EIP-3607 are Profile 1 conditions and do not apply to it.</remarks>
    private static bool CouldIncludeTx(Transaction tx, Block block, IReadOnlyStateProvider state, IReleaseSpec spec, ITxValidator txValidator, ref Dictionary<AddressAsKey, AccountStruct>? accountCache)
    {
        if (tx.SenderAddress is null) return false;
        if (!FitsRemainingBlockGas(tx, block, spec)) return false;
        // Appendability must match normal execution, so reuse the full well-formedness check, not a subset.
        if (!txValidator.IsWellFormed(tx, spec, block.GasLimit)) return false;
        if (tx.MaxFeePerGas < block.BaseFeePerGas) return false;
        if (tx.SupportsBlobs
            && (BlobGasCalculator.CalculateBlobGas(tx) > spec.GasCosts.MaxBlobGasPerBlock - (block.Header.BlobGasUsed ?? 0)
                || !BlobGasCalculator.TryCalculateFeePerBlobGas(block.Header, spec.BlobBaseFeeUpdateFraction, out UInt256 blobBaseFee)
                || (tx.MaxFeePerBlobGas ?? UInt256.Zero) < blobBaseFee))
            return false;

        // A sponsored frame transaction is paid by the payer its validation prefix nominates, not by its
        // sender. Always recomputed, never read from Transaction.PayerAddress, so the two cannot disagree.
        Address payer = tx.SupportsFrames ? FrameTxValidation.GetPrefixPaymaster(tx) ?? tx.SenderAddress : tx.SenderAddress;

        accountCache ??= [];
        ref AccountStruct account = ref CollectionsMarshal.GetValueRefOrAddDefault(accountCache, payer, out bool cached);
        // Cache the negative result too (default struct = balance 0, nonce 0, empty codehash).
        if (!cached) state.TryGetAccount(payer, out account);

        // A frame transaction reserves max_gas and its EIP-8141 maximum cost, both of which the forms below
        // under-count by at least the intrinsic cost; its sender is a smart account EIP-3607 does not bar.
        if (tx.SupportsFrames)
        {
            return FrameTxValidation.TryCalculateGasBudget(tx, spec, out _, out _, out ulong maxGas)
                   && (spec.IsEip8037Enabled || maxGas <= block.GasLimit - block.GasUsed)
                   && FrameTxValidation.TryCalculateMaxCost(tx, spec, out UInt256 frameCost)
                   && SpendableBalance(block, payer, in account) >= frameCost;
        }

        // EIP-3607: a sender with non-delegated code cannot send a tx.
        if (account.HasCode && !state.IsDelegatedCode(payer)) return false;

        // Overflow-checked like TransactionProcessor.BuyGas: an adversarial MaxFeePerGas must not wrap the cost.
        if (UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 txCost)
            || UInt256.AddOverflow(txCost, tx.Value, out txCost))
            return false;

        // A blob tx must also cover maxFeePerBlobGas × blob gas up front, or it could never have executed.
        if (tx.SupportsBlobs
            && (!BlobGasCalculator.TryCalculateBlobMaxFee(tx.BlobVersionedHashes?.Length ?? 0, tx.MaxFeePerBlobGas ?? UInt256.Zero, out UInt256 blobFee)
                || UInt256.AddOverflow(txCost, blobFee, out txCost)))
            return false;

        return SpendableBalance(block, payer, in account) >= txCost && account.Nonce == tx.Nonce;
    }

    /// <summary>Whether an appended transaction's worst-case block gas still fits.</summary>
    /// <remarks>EIP-8037 admits a transaction only if both its execution and its state reservation fit the
    /// matching dimension, so measuring it against the header's max(execution, state) rejects transactions the
    /// spec judges includable. Callers whose block carries no dimensions must not put an EIP-8037 block to this
    /// check — the max alone under-reports censorship — which is why the engine API declines to answer instead.</remarks>
    private static bool FitsRemainingBlockGas(Transaction tx, Block block, IReleaseSpec spec)
    {
        // Subtract on the block side: GasUsed <= GasLimit is invariant, so this cannot underflow the
        // way GasLimit - tx.GasLimit would for an oversized tx.
        if (!spec.IsEip8037Enabled) return tx.GasLimit <= block.GasLimit - block.GasUsed;

        (ulong execution, ulong state) = block.Header.GasUsedPerDimension ?? (block.GasUsed, block.GasUsed);
        return Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(tx, spec, out ulong executionReservation, out ulong stateReservation)
            && Eip8037BlockGasInclusionCheck.Validate(block.GasLimit, execution, state, executionReservation, stateReservation)
                == Eip8037BlockGasInclusionCheck.Outcome.Ok;
    }

    /// <summary>Balance the sender would have had when an appended transaction executed.</summary>
    /// <remarks>Withdrawals are the only post-merge credit applied after the block's transactions, so counting
    /// them into the post-block balance would make an honest proposer look like a censor.</remarks>
    private static UInt256 SpendableBalance(Block block, Address sender, ref readonly AccountStruct account)
    {
        UInt256 balance = account.Balance;
        if (block.Withdrawals is not { Length: > 0 }) return balance;

        foreach (Withdrawal withdrawal in block.Withdrawals)
        {
            if (withdrawal.Address != sender) continue;
            if (UInt256.SubtractUnderflow(balance, withdrawal.AmountInWei, out balance)) return UInt256.Zero;
        }

        return balance;
    }
}
