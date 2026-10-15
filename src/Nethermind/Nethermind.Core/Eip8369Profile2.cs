// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Core;

/// <summary>Why a frame transaction is not an EIP-8369 Profile 2 candidate, or <see cref="None"/> when it is.</summary>
public enum Profile2Exclusion : byte
{
    /// <summary>The transaction meets every Profile 2 candidate condition.</summary>
    None = 0,

    /// <summary>It carries blob hashes: blob gas has its own budget, over which EIP-8369 defines no omission check.</summary>
    CarriesBlobs,

    /// <summary>Its validation prefix matches none of the four admitted shapes, or a prefix frame carries
    /// <c>ATOMIC_BATCH_FLAG</c>.</summary>
    UnrecognizedPrefix,

    /// <summary>A frame behind the validation prefix has mode <c>VERIFY</c>.</summary>
    VerifyFrameAfterPrefix,

    /// <summary>Its VERIFY budget cost exceeds <c>MAX_VERIFY_GAS_PER_TX</c>.</summary>
    VerifyBudgetExceeded,
}

/// <summary>
/// The EIP-8369 Profile 2 (FOCIL AA-VOPS) candidate conditions, which decide whether FOCIL enforces the
/// omission of an EIP-8141 frame transaction from a block.
/// https://eips.ethereum.org/EIPS/eip-8369
/// </summary>
/// <remarks>
/// Candidacy is a pure function of the transaction: it reads no state and no release spec. Profile 2
/// <em>eligibility</em> is the separate, state-dependent half — keyed nonces, recent roots, and a validation
/// prefix replayed within the AA-VOPS surface — and cannot be decided here.
/// </remarks>
public static class Eip8369Profile2
{
    /// <summary>Whether EIP-7805's Profile 2 is active: it needs EIP-8141, EIP-8250 and EIP-8272 beside inclusion lists.</summary>
    /// <remarks>EIP-7805 activates both profiles with FOCIL and requires these EIPs for that activation, so the
    /// conjunction only matters for a chain that schedules inclusion lists without frames, which keeps the original
    /// Profile 1 rule. Once Profile 2 is active, an omitted transaction outside both profiles, a blob transaction among
    /// them, is excused.</remarks>
    public static bool IsActive(IReleaseSpec spec) =>
        spec.InclusionListsEnabled && spec.IsEip8141Enabled && spec.IsEip8250Enabled && spec.IsEip8272Enabled;

    /// <summary>Classifies <paramref name="transaction"/> against the EIP-8369 Profile 2 candidate conditions.</summary>
    /// <remarks>
    /// Condition 1, static EIP-8141 validity, is the caller's: every caller already runs a well-formedness
    /// check, and repeating it here would need the release spec this deliberately does not take. Condition 3's
    /// batch half is folded into <see cref="Profile2Exclusion.UnrecognizedPrefix"/> because the shape
    /// predicates compare the whole flags byte, so a prefix frame carrying <c>ATOMIC_BATCH_FLAG</c> already
    /// matches no admitted shape.
    /// </remarks>
    /// <param name="transaction">The frame transaction to classify; a transaction without frames is
    /// reported as <see cref="Profile2Exclusion.UnrecognizedPrefix"/> rather than thrown on.</param>
    /// <param name="maxVerifyGasPerTx"><c>MAX_VERIFY_GAS_PER_TX</c>; <c>0</c> lifts the cap.</param>
    /// <returns><see cref="Profile2Exclusion.None"/> when every candidate condition holds.</returns>
    public static Profile2Exclusion Classify(Transaction transaction, ulong maxVerifyGasPerTx)
    {
        if (transaction.CarriesBlobs) return Profile2Exclusion.CarriesBlobs;

        if (FrameTxValidation.RecognizedPrefixLength(transaction.Frames ?? [], transaction.SenderAddress) is null)
            return Profile2Exclusion.UnrecognizedPrefix;

        if (FrameTxValidation.HasVerifyFrameAfterPrefix(transaction)) return Profile2Exclusion.VerifyFrameAfterPrefix;

        return maxVerifyGasPerTx != 0 && VerifyBudgetCost(transaction) > maxVerifyGasPerTx
            ? Profile2Exclusion.VerifyBudgetExceeded
            : Profile2Exclusion.None;
    }

    /// <summary>
    /// EIP-7805 <c>verify_cost(T)</c>: "the EIP-8141 <c>signature_verification_cost</c> of <c>T</c> plus the
    /// <c>limits.execution</c> of every frame of its validation prefix", the expiry verifier frame included.
    /// </summary>
    /// <remarks>The prefix's <c>limits.state</c> does not count toward the VERIFY budget.</remarks>
    public static ulong VerifyBudgetCost(Transaction transaction) => FrameTxValidation.ValidationWorkGas(transaction);

    /// <summary>EIP-7805 § VERIFY budget: the Profile 2 entries the per-IL VERIFY budget fill admits.</summary>
    /// <remarks>
    /// Each list starts from <see cref="Eip8369Constants.MaxVerifyGasPerIl"/> and takes its entries in order. An
    /// entry that is no Profile 2 candidate, statically invalid or costlier than the remaining budget is skipped
    /// without a debit; otherwise the signature cost is debited, and the prefix cost too once the signatures verify.
    /// An entry is admitted when any list admits it, so one list exhausting its own budget cannot excuse another's.
    /// An entry carried by several positions is verified once, however many lists carry it, but debited by each.
    /// </remarks>
    /// <param name="inclusionLists">Each committee member's occurrences, in the order the fill processes them.</param>
    /// <param name="maxVerifyGasPerTx"><c>MAX_VERIFY_GAS_PER_TX</c>; <c>0</c> lifts the cap.</param>
    /// <param name="signaturesValid">Verifies an occurrence's protocol signatures; <c>null</c> takes them as valid.</param>
    /// <param name="staticallyValid">The active fork's noncryptographic static checks; <c>null</c> takes them as passed.</param>
    public static HashSet<Hash256AsKey> AdmitByVerifyBudget(
        IEnumerable<IEnumerable<Transaction>> inclusionLists, ulong maxVerifyGasPerTx, Func<Transaction, bool>? signaturesValid,
        Func<Transaction, bool>? staticallyValid = null)
    {
        HashSet<Hash256AsKey> admitted = [];
        Dictionary<Hash256AsKey, bool>? verified = null;
        Dictionary<Hash256AsKey, bool>? valid = null;
        foreach (IEnumerable<Transaction> inclusionList in inclusionLists)
        {
            ulong remaining = Eip8369Constants.MaxVerifyGasPerIl;
            foreach (Transaction tx in inclusionList)
            {
                if (!tx.SupportsFrames || tx.Hash is null) continue;
                // Step 1: the noncryptographic candidate conditions, failures skipped without a debit.
                if (Classify(tx, maxVerifyGasPerTx) != Profile2Exclusion.None || !IsStaticallyValid(tx, tx.Hash, staticallyValid, ref valid)) continue;

                ulong cost = VerifyBudgetCost(tx);
                if (cost > remaining) continue;

                ulong signatureCost = FrameTxValidation.SignatureVerificationWorkGas(tx);
                remaining -= signatureCost;
                // A failed signature keeps its debit but is not admitted.
                if (signaturesValid is not null && !AreSignaturesValid(tx, tx.Hash, signaturesValid, ref verified)) continue;

                remaining -= cost - signatureCost;
                admitted.Add(tx.Hash);
            }
        }
        return admitted;
    }

    private static bool IsStaticallyValid(Transaction tx, Hash256 hash, Func<Transaction, bool>? staticallyValid, ref Dictionary<Hash256AsKey, bool>? valid)
    {
        if (staticallyValid is null) return true;
        valid ??= [];
        ref bool result = ref CollectionsMarshal.GetValueRefOrAddDefault(valid, hash, out bool exists);
        if (!exists) result = staticallyValid(tx);
        return result;
    }

    private static bool AreSignaturesValid(Transaction tx, Hash256 hash, Func<Transaction, bool> signaturesValid, ref Dictionary<Hash256AsKey, bool>? verified)
    {
        verified ??= [];
        ref bool valid = ref CollectionsMarshal.GetValueRefOrAddDefault(verified, hash, out bool exists);
        if (!exists) valid = signaturesValid(tx);
        return valid;
    }
}
