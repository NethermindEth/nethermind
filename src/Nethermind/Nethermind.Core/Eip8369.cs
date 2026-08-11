// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>The EIP-8369 FOCIL enforcement profile of a transaction.</summary>
public enum FocilProfile
{
    /// <summary>Regular non-frame, non-blob transaction (legacy/2930/1559/7702); end-of-payload omission check, no VERIFY budget.</summary>
    One,

    /// <summary>EIP-8141 frame transaction with a recognized validation-prefix shape, no blobs, and VERIFY cost within budget.</summary>
    Two,

    /// <summary>Outside FOCIL enforcement: blob-carrying (incl. blob frame txs), wrong-shape, over-budget, or otherwise non-enforceable.</summary>
    Outside,
}

/// <summary>
/// EIP-8369 classification and includer VERIFY-budget helpers spanning EIP-7805 (FOCIL) and
/// EIP-8141 (frame transactions). https://eips.ethereum.org/EIPS/eip-8369
/// </summary>
public static class Eip8369
{
    /// <summary>Classifies <paramref name="tx"/> into its EIP-8369 FOCIL enforcement profile.</summary>
    /// <remarks>
    /// Profile 2 requires all of: a frame transaction, no blobs, a recognized validation-prefix shape, no
    /// VERIFY-mode frame after the prefix, and a cost within <see cref="Eip8369Constants.MaxVerifyGasPerTx"/>.
    /// </remarks>
    public static FocilProfile Classify(Transaction tx)
    {
        bool carriesBlobs = tx.BlobVersionedHashes is { Length: > 0 };

        if (!tx.SupportsFrames)
        {
            // Regular non-frame tx: Profile 1 unless it carries blobs (type-3), which is outside enforcement.
            return carriesBlobs || tx.Type == TxType.Blob ? FocilProfile.Outside : FocilProfile.One;
        }

        // Frame transaction. Blob-carrying frame txs are outside enforcement.
        if (carriesBlobs) return FocilProfile.Outside;

        if (!FrameTxValidation.TryGetValidationPrefixLength(tx, out int prefixLength)) return FocilProfile.Outside;

        // No VERIFY-mode frame may follow the recognized prefix.
        TxFrame[] frames = tx.Frames ?? [];
        for (int i = prefixLength; i < frames.Length; i++)
        {
            if (frames[i].Mode == TxFrame.ModeVerify) return FocilProfile.Outside;
        }

        // Reuse the prefix length already resolved above so the frame-shape walk runs once.
        return FrameTxValidation.ValidationWorkGas(tx, prefixLength) <= Eip8369Constants.MaxVerifyGasPerTx
            ? FocilProfile.Two
            : FocilProfile.Outside;
    }

    /// <summary>
    /// The VERIFY-budget cost the includer charges a Profile-2 transaction: the declared gas limits of
    /// every frame in its validation prefix (incl. the optional expiry frame) plus its signature-verification gas.
    /// </summary>
    /// <param name="tx">The Profile-2 frame transaction to price.</param>
    public static ulong Profile2VerifyCost(Transaction tx) => FrameTxValidation.ValidationWorkGas(tx);
}
