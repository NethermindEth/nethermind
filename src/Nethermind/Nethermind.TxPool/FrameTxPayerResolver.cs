// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

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

        int index = FrameTxValidation.ApprovalSearchStart(frames);

        // Re-checked here, though FrameTxPayerlessFilter already rejects these, so a direct caller still gets NoPayer.
        if (IsStructurallyPayerless(frames, sender, index))
        {
            return Unresolved(FrameTxPayerOutcome.NoPayer);
        }

        TxFrame verifyFrame = frames[index];

        // Self relay: a self_verify frame approves both sender and payer, so the payer is the sender.
        if (FrameTxValidation.IsSelfVerifyFrame(verifyFrame, sender))
        {
            // A deployed or EIP-7702-delegated sender runs its own account code and must be simulated, as does
            // a prefix ahead of this frame that cannot be priced from the frame list alone.
            if (senderHasCode || !LeadingFramesProvablyRun(frames, index))
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            // A following pay frame overrides the payer once the sender's balance drops below max cost.
            if (HasFollowingPaymentFrame(frames, index))
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            // The default code's mandatory charges have to be provably covered, or the frame halts on gas
            // where the shortcut called it approved.
            if (!DefaultCodeChargesAreCovered(verifyFrame, tx, in senderAccount))
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

        return IsStructurallyPayerless(frames, sender, FrameTxValidation.ApprovalSearchStart(frames));
    }

    /// <summary>Structural NoPayer decision over a prefix whose optional leading expiry and deploy frames are already skipped to <paramref name="index"/>.</summary>
    /// <remarks>An empty remainder (a lone expiry or deploy frame), or a prefix ending in an only_verify frame, never approves a payer.</remarks>
    private static bool IsStructurallyPayerless(TxFrame[] frames, Address sender, int index) =>
        index >= frames.Length
        || (FrameTxValidation.IsOnlyVerifyFrame(frames[index], sender) && index + 1 >= frames.Length);


    /// <summary>Whether every frame the prologue skipped ahead of <paramref name="index"/> provably runs to
    /// completion, its failure being what would invalidate the transaction the approving frame is named payer of.</summary>
    /// <remarks>
    /// Only a leading expiry frame is priceable from the frame list, at <see cref="Eip8141Constants.ExpiryFrameExecutionGas"/>.
    /// Whether its deadline has passed is <see cref="Filters.ExpiredFrameTxFilter"/>'s question.
    /// A leading deploy frame defers whatever it budgets: by the time the VERIFY frame runs it has installed code
    /// at tx.sender, so the default-code inference would read the wrong account.
    /// </remarks>
    private static bool LeadingFramesProvablyRun(TxFrame[] frames, int index) =>
        index switch
        {
            0 => true,
            1 => FrameTxValidation.IsExpiryVerifyFrame(frames[0])
                 && frames[0].ExecutionGasLimit >= Eip8141Constants.ExpiryFrameExecutionGas,
            _ => false,
        };

    /// <summary>Whether a default-code <c>VERIFY</c> frame provably affords everything running it charges.</summary>
    /// <remarks>
    /// The frame pays its target's access before dispatch, warm because the transaction warms its sender, and
    /// the default code itself draws no further execution gas — the boundary <c>Execute_DefaultCodeFrame_PaysItsTargetAccess</c>
    /// and <c>Execute_DefaultCodeFrameGasBelowItsTargetAccess_InvalidatesTheTransaction</c> pin. Both arms only
    /// ever defer, so a charge growing past what this knows costs a simulation rather than admitting what
    /// execution rejects.
    /// </remarks>
    private static bool DefaultCodeChargesAreCovered(TxFrame verifyFrame, Transaction tx, in AccountStruct senderAccount) =>
        verifyFrame.ExecutionGasLimit >= Eip8038Constants.WarmAccess
        && NonceStateGasIsCovered(verifyFrame, tx, in senderAccount);

    /// <summary>Whether <paramref name="verifyFrame"/> budgets the state gas the approval's nonce consumption
    /// can owe, mirroring <c>FrameTxContext.NonceStateGas</c> at its worst case.</summary>
    /// <remarks>The branches are exclusive as they are there, and both bounds read no chain state: a keyed set
    /// never creates the sender and writes at most one <c>NONCE_MANAGER</c> slot per key, while the account-nonce
    /// set (<c>null</c> or <c>[0]</c>) writes no slot and owes a creation only for a sender that does not exist —
    /// which an account reading back as empty may be.</remarks>
    private static bool NonceStateGasIsCovered(TxFrame verifyFrame, Transaction tx, in AccountStruct senderAccount)
    {
        ulong worstCase = tx.NonceKeys is { } nonceKeys && KeyedNonceManager.UsesKeyedDomain(nonceKeys)
            ? (ulong)nonceKeys.Length * (ulong)GasCostOf.SSetState
            : senderAccount.IsTotallyEmpty ? (ulong)GasCostOf.NewAccountState : 0;

        return worstCase <= verifyFrame.StateGasLimit;
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
