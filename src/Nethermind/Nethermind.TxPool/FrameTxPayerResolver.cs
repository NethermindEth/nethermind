// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>Resolves the fee-payer of an EIP-8141 frame transaction from account state alone, without running any code.</summary>
/// <remarks>A <see cref="FrameTxPayerOutcome.Resolved"/> payer is only trustworthy once <see cref="Filters.FrameTxSignatureFilter"/> has run; everything unrecognised defers to simulation.</remarks>
internal static class FrameTxPayerResolver
{
    /// <param name="senderAccount">The sender's chain-head account, already fetched by the caller.</param>
    public static FrameTxPayerResolution Resolve(Transaction tx, in AccountStruct senderAccount)
    {
        TxFrame[]? frames = tx.Frames;
        Address? sender = tx.SenderAddress;
        if (frames is null || frames.Length == 0 || sender is null)
        {
            return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
        }

        TxFrameSignature[] signatures = tx.FrameSignatures ?? [];

        // A never-seen account is the zeroed struct whose code hash isn't empty-keccak, so HasCode
        // would wrongly report code on it.
        bool senderHasCode = !senderAccount.IsNull && senderAccount.HasCode;

        int index = PrefixVerifyIndex(frames);

        // Re-checked here, though FrameTxPayerlessFilter already rejects these, so a direct caller still gets NoPayer.
        if (IsStructurallyPayerless(frames, sender, index))
        {
            return Unresolved(FrameTxPayerOutcome.NoPayer);
        }

        TxFrame verifyFrame = frames[index];

        // Self relay: a self_verify frame approves both sender and payer, so the payer is the sender.
        if (FrameTxValidation.IsSelfVerifyFrame(verifyFrame, sender))
        {
            // A deployed or EIP-7702-delegated sender runs its own account code and must be simulated. So
            // does a codeless one behind a deploy frame: by the time the VERIFY frame runs, that frame has
            // installed code at tx.sender, so the default-code inference below reads the wrong account.
            if (senderHasCode || (index > 0 && FrameTxValidation.IsDeployFrame(frames[index - 1])))
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            // A following pay frame overrides the payer once the sender's balance drops below max cost.
            if (HasFollowingPaymentFrame(frames, index))
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            // A non-matching signature shape is not proof of invalidity, so defer rather than drop.
            return DefaultCodeApproves(signatures, sender)
                ? new FrameTxPayerResolution(FrameTxPayerOutcome.Resolved, sender)
                : Unresolved(FrameTxPayerOutcome.RequiresSimulation);
        }

        // Everything else is opaque: the pool cannot verify a third-party pay-frame signature at admission.
        return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
    }

    private static FrameTxPayerResolution Unresolved(FrameTxPayerOutcome outcome) => new(outcome, null);

    /// <summary>Signature- and state-free test of whether a validation prefix provably never approves a payer.</summary>
    public static bool IsStructurallyPayerless(Transaction tx)
    {
        TxFrame[]? frames = tx.Frames;
        Address? sender = tx.SenderAddress;
        if (frames is null || frames.Length == 0 || sender is null)
        {
            return false;
        }

        return IsStructurallyPayerless(frames, sender, PrefixVerifyIndex(frames));
    }

    /// <summary>Structural NoPayer decision over a prefix whose optional leading expiry and deploy frames are already skipped to <paramref name="index"/>.</summary>
    /// <remarks>An empty remainder (a lone expiry or deploy frame), or a prefix ending in an only_verify frame, never approves a payer.</remarks>
    private static bool IsStructurallyPayerless(TxFrame[] frames, Address sender, int index) =>
        index >= frames.Length
        || (FrameTxValidation.IsOnlyVerifyFrame(frames[index], sender) && index + 1 >= frames.Length);

    /// <summary>The index of the VERIFY frame that names the payer, skipping an optional leading expiry_verify and deploy frame.</summary>
    /// <remarks>The same prefix grammar <see cref="FrameTxValidation.ValidationWorkGas"/> prices admission against, so the two cannot drift.</remarks>
    private static int PrefixVerifyIndex(TxFrame[] frames)
    {
        int index = FrameTxValidation.IsExpiryVerifyFrame(frames[0]) ? 1 : 0;
        if (index < frames.Length && FrameTxValidation.IsDeployFrame(frames[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>Structural check that index-0 is a canonical-hash (empty <c>msg</c>) secp256k1 signature by the sender.</summary>
    /// <remarks>Cryptographic verification is a separate upstream gate.</remarks>
    private static bool DefaultCodeApproves(TxFrameSignature[] signatures, Address sender)
    {
        if (signatures.Length == 0)
        {
            return false;
        }

        TxFrameSignature signature = signatures[0];
        return signature.Scheme == TxFrameSignature.SchemeSecp256k1
               && signature.Msg.IsEmpty
               && (signature.Signer ?? sender) == sender;
    }

    private static bool HasFollowingPaymentFrame(TxFrame[] frames, int verifyIndex)
    {
        for (int i = verifyIndex + 1; i < frames.Length; i++)
        {
            if ((frames[i].Flags & TxFrame.ApprovePayment) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
