// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

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

        TxFrame[] frames = transaction.Frames ?? [];
        if (FrameTxValidation.RecognizedPrefixLength(frames, transaction.SenderAddress) is not int prefixLength)
            return Profile2Exclusion.UnrecognizedPrefix;

        if (FrameTxValidation.HasVerifyFrameAfterPrefix(transaction)) return Profile2Exclusion.VerifyFrameAfterPrefix;

        return maxVerifyGasPerTx != 0 && VerifyBudgetCost(transaction, frames, prefixLength) > maxVerifyGasPerTx
            ? Profile2Exclusion.VerifyBudgetExceeded
            : Profile2Exclusion.None;
    }

    /// <summary>The static EIP-8369 VERIFY budget cost of a recognized prefix: every declared frame limit it
    /// spans, the optional expiry verifier frame included, plus the signature verification gas EIP-8141 counts.</summary>
    private static ulong VerifyBudgetCost(Transaction transaction, TxFrame[] frames, int prefixLength)
    {
        ulong total = FrameTxValidation.SignatureVerificationWorkGas(transaction);
        for (int i = 0; i < prefixLength; i++)
        {
            total = total.SaturatingAdd(frames[i].ExecutionGasLimit.SaturatingAdd(frames[i].StateGasLimit));
        }

        return total;
    }
}
