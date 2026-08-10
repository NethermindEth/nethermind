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
/// Only the default-code <c>self_verify</c> prefix (optionally preceded by an <c>expiry_verify</c>
/// frame) resolves natively to the sender; everything else defers to
/// <see cref="FrameTxPayerOutcome.RequiresSimulation"/>. A third-party payer is never named natively,
/// so a forged frame signature cannot resolve to an arbitrary victim.
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

        // Optional leading expiry_verify frame: skipped for shape matching, but its deadline and the
        // EXPIRY_VERIFIER code join the dependency set.
        int index = 0;
        bool dependsOnExpiry = false;
        ulong expiryDeadline = 0;
        ValueHash256 expiryCodeHash = default;
        if (IsExpiryVerifyFrame(frames[0]))
        {
            dependsOnExpiry = true;
            expiryDeadline = BinaryPrimitives.ReadUInt64BigEndian(frames[0].Data.Span);
            expiryCodeHash = state.GetCodeHash(Eip8141Constants.ExpiryVerifierAddress);
            index = 1;
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

        // A prefix with only an expiry frame never sets a payer.
        if (index >= frames.Length)
        {
            return Unresolved(FrameTxPayerOutcome.NoPayer);
        }

        TxFrame verifyFrame = frames[index];

        // Self relay: a self_verify frame approves both sender and payer, so the payer is the sender.
        if (IsSelfVerify(verifyFrame, sender))
        {
            // Legible only for a default-code sender; a deployed or EIP-7702-delegated sender runs its
            // own account code and must be simulated.
            if (senderHasCode)
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            // A non-matching signature shape isn't proof of invalidity — where the signature belongs is
            // unsettled — so defer the verdict to execution rather than dropping the tx at admission.
            return DefaultCodeApproves(signatures, sender)
                ? ResolvedTo(sender, in senderCodeHash, senderAccount.Balance)
                : Unresolved(FrameTxPayerOutcome.RequiresSimulation);
        }

        // only_verify approves execution but not payment; a following pay frame sets the payer.
        if (IsOnlyVerify(verifyFrame, sender))
        {
            // A lone only_verify frame has no payment-approving frame after it, so it provably never sets
            // a payer regardless of signature or sender code — a structural NoPayer.
            if (index + 1 >= frames.Length)
            {
                return Unresolved(FrameTxPayerOutcome.NoPayer);
            }

            // A following pay frame names a third-party payer whose signature the pool cannot verify at
            // admission; deferred to the signature-verification / simulation layer.
            return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
        }

        // Not a recognized legible prefix (e.g. a deploy frame, or an unrecognized VERIFY shape).
        return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
    }

    /// <summary>
    /// Structural check that index-0 is a canonical-hash (empty <c>msg</c>) secp256k1 signature whose
    /// signer is the sender. Cryptographic verification is a separate deferred gate.
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

    private static bool IsExpiryVerifyFrame(TxFrame frame) =>
        frame.Mode == TxFrame.ModeVerify
        && frame.Flags == 0
        && frame.Value.IsZero
        && frame.Target == Eip8141Constants.ExpiryVerifierAddress
        && frame.Data.Length == Eip8141Constants.ExpiryDataLength;

    private static bool IsSelfVerify(TxFrame frame, Address sender) =>
        frame.Mode == TxFrame.ModeVerify
        && frame.Flags == TxFrame.ApproveExecutionAndPayment
        && (frame.Target is null || frame.Target == sender);

    private static bool IsOnlyVerify(TxFrame frame, Address sender) =>
        frame.Mode == TxFrame.ModeVerify
        && frame.Flags == TxFrame.ApproveExecution
        && (frame.Target is null || frame.Target == sender);
}
