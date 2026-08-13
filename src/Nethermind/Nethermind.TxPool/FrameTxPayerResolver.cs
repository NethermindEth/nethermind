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
/// <remarks>A <see cref="FrameTxPayerOutcome.Resolved"/> payer is only trustworthy once
/// <see cref="Filters.FrameTxSignatureFilter"/> has verified the frame signatures upstream.</remarks>
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
        // Copied out of the in-parameter so the local functions below can close over it.
        ulong senderNonce = senderAccount.Nonce;

        int index = PrefixVerifyIndex(frames);

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

        if (IsStructurallyPayerless(frames, sender, index))
        {
            return Unresolved(FrameTxPayerOutcome.NoPayer);
        }

        TxFrame verifyFrame = frames[index];

        // Self relay: a self_verify frame approves both sender and payer, so the payer is the sender.
        if (FrameTxValidation.IsSelfVerifyFrame(verifyFrame, sender))
        {
            // A deployed or EIP-7702-delegated sender runs its own account code, so it has to be simulated.
            if (senderHasCode)
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            // A following pay frame can override the payer when the sender's balance drops below max cost.
            if (HasFollowingPaymentFrame(frames, index))
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            return DefaultCodeApproves(signatures, sender)
                ? ResolvedTo(sender, in senderCodeHash, senderAccount.Balance)
                : Unresolved(FrameTxPayerOutcome.RequiresSimulation);
        }

        return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
    }

    /// <summary>
    /// Signature- and state-free test of whether a validation prefix provably never approves a payer.
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

    /// <summary>Structural NoPayer decision over a prefix already advanced past its leading frames to <paramref name="index"/>.</summary>
    private static bool IsStructurallyPayerless(TxFrame[] frames, Address sender, int index) =>
        // Nothing left after the leading frames, or a prefix ending in only_verify, never approves a payer.
        index >= frames.Length
        || (FrameTxValidation.IsOnlyVerifyFrame(frames[index], sender) && index + 1 >= frames.Length);

    /// <summary>
    /// Index of the VERIFY frame naming the payer, skipping the optional leading expiry_verify and deploy
    /// frames — the same grammar <see cref="FrameTxValidation.ValidationWorkGas"/> prices admission against.
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
    /// signer is the sender; cryptographic verification is a separate upstream gate.
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
