// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// Resolves the fee-payer of an EIP-8141 frame transaction at mempool admission by evaluating its
/// legible validation prefix from account state (code hash, nonce, balance) without running any code.
/// </summary>
/// <remarks>
/// Only a default-code <c>self_verify</c> prefix with no following payment frame (optionally preceded
/// by an <c>expiry_verify</c> and/or a deploy frame) resolves natively to the sender; everything else
/// defers to <see cref="FrameTxPayerOutcome.RequiresSimulation"/>. Frame signatures are cryptographically verified
/// upstream by <see cref="Filters.FrameTxSignatureFilter"/>, which is a precondition of this resolver: a
/// <see cref="FrameTxPayerOutcome.Resolved"/> payer is only trustworthy given that filter has run, so a
/// direct caller skipping it would break the guarantee.
/// </remarks>
internal static class FrameTxPayerResolver
{
    /// <param name="senderAccount">The sender's chain-head account, already fetched by the caller.</param>
    public static FrameTxPayerResolution Resolve(Transaction tx, IReadOnlyStateProvider state, in AccountStruct senderAccount)
    {
        TxFrame[]? frames = tx.Frames;
        Address? sender = tx.SenderAddress;
        if (frames is null || frames.Length == 0 || sender is null)
        {
            return new FrameTxPayerResolution(FrameTxPayerOutcome.RequiresSimulation, null, default);
        }

        TxFrameSignature[] signatures = tx.FrameSignatures ?? [];

        // A never-seen account is the zeroed struct whose code hash isn't empty-keccak; normalize both.
        bool senderExists = !senderAccount.IsNull;
        bool senderHasCode = senderExists && senderAccount.HasCode;
        ValueHash256 senderCodeHash = senderExists ? senderAccount.CodeHash : Keccak.OfAnEmptyString.ValueHash256;
        // Copied out of the in-parameter so the local-function dependency-set builders can close over it.
        ulong senderNonce = senderAccount.Nonce;

        // The recognized validation prefix may lead with an optional expiry_verify frame and/or a deploy
        // frame; both are skipped to reach the VERIFY frame that names the payer, matching the grammar
        // FrameTxValidation prices admission against so the pricing filter and this verdict cannot drift.
        int index = PrefixVerifyIndex(frames);

        // A leading expiry_verify frame contributes its deadline and the EXPIRY_VERIFIER code to the
        // dependency set.
        bool dependsOnExpiry = false;
        ulong expiryDeadline = 0;
        ValueHash256 expiryCodeHash = default;
        if (FrameTxValidation.IsExpiryVerifyFrame(frames[0]))
        {
            dependsOnExpiry = true;
            expiryDeadline = BinaryPrimitives.ReadUInt64BigEndian(frames[0].Data.Span);
            expiryCodeHash = state.GetCodeHash(Eip8141Constants.ExpiryVerifierAddress);
        }

        FrameTxPayerResolution Unresolved(FrameTxPayerOutcome outcome) =>
            new(outcome, null, new FrameTxDependencySet(
                senderCodeHash, senderNonce,
                payer: null, default, default,
                dependsOnExpiry, expiryDeadline, expiryCodeHash));

        FrameTxPayerResolution ResolvedTo(Address payer, in ValueHash256 payerCodeHash, in UInt256 payerBalance) =>
            new(FrameTxPayerOutcome.Resolved, payer, new FrameTxDependencySet(
                senderCodeHash, senderNonce,
                payer, payerCodeHash, payerBalance,
                dependsOnExpiry, expiryDeadline, expiryCodeHash));

        // Structural payerless prefixes are rejected pre-signature by FrameTxPayerlessFilter; re-checked
        // here via the shared predicate so a direct caller still gets a NoPayer verdict.
        if (IsStructurallyPayerless(frames, sender, index))
        {
            return Unresolved(FrameTxPayerOutcome.NoPayer);
        }

        TxFrame verifyFrame = frames[index];

        // Self relay: a self_verify frame approves both sender and payer, so the payer is the sender.
        if (FrameTxValidation.IsSelfVerifyFrame(verifyFrame, sender))
        {
            // Legible only for a default-code sender; a deployed or EIP-7702-delegated sender runs its
            // own account code and must be simulated. Read at chain head, so code a preceding deploy
            // frame installs is not reflected — that changes what runs, never who pays.
            if (senderHasCode)
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            // A following pay frame can override the payer: if the sender's balance drops below max cost
            // the self approval is skipped and the pay frame names a sponsor, so defer to simulation.
            if (HasFollowingPaymentFrame(frames, index))
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            // A non-matching signature shape isn't proof of invalidity — where the signature belongs is
            // unsettled — so defer the verdict to execution rather than dropping the tx at admission.
            return DefaultCodeApproves(signatures, sender)
                ? ResolvedTo(sender, in senderCodeHash, senderAccount.Balance)
                : Unresolved(FrameTxPayerOutcome.RequiresSimulation);
        }

        // Everything else defers to simulation: an only_verify frame names a third-party payer whose
        // pay-frame signature the pool cannot verify at admission (a lone only_verify is a structural
        // NoPayer, already handled above), and any other unrecognized VERIFY shape is likewise opaque.
        return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
    }

    /// <summary>
    /// Signature- and state-free test of whether a frame transaction's validation prefix provably never
    /// approves a payer (a lone expiry frame, or a prefix ending in an <c>only_verify</c> frame), letting
    /// the pool drop it before spending elliptic-curve work on its signature list.
    /// </summary>
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

    /// <summary>Structural NoPayer decision over an already-parsed prefix, with the optional leading expiry and deploy frames skipped to <paramref name="index"/>.</summary>
    private static bool IsStructurallyPayerless(TxFrame[] frames, Address sender, int index) =>
        // Only a leading prefix that never reaches a payment approval — an empty remainder (e.g. a lone
        // expiry or deploy frame), or a prefix ending in an only_verify frame — never approves a payer.
        index >= frames.Length
        || (FrameTxValidation.IsOnlyVerifyFrame(frames[index], sender) && index + 1 >= frames.Length);

    /// <summary>
    /// The index of the VERIFY frame that names the payer, reached by skipping an optional leading
    /// expiry_verify frame and an optional deploy frame — the same prefix grammar
    /// <see cref="FrameTxValidation.ValidationWorkGas"/> prices admission against.
    /// </summary>
    private static int PrefixVerifyIndex(TxFrame[] frames)
    {
        int index = FrameTxValidation.IsExpiryVerifyFrame(frames[0]) ? 1 : 0;
        if (index < frames.Length && FrameTxValidation.IsDeployFrame(frames[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Structural check that index-0 is a canonical-hash (empty <c>msg</c>) secp256k1 signature whose
    /// signer is the sender. Cryptographic verification is a separate upstream gate.
    /// </summary>
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
