// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.TxPool;

/// <summary>
/// Resolves the fee-payer of an EIP-8141 frame transaction at mempool admission by directly
/// evaluating the protocol-defined semantics of its <em>legible</em> validation prefix — the
/// recognized public-mempool prefix shapes whose payer is decidable from account state
/// (<c>code hash</c>, <c>nonce</c>, <c>balance</c>) with no deployed code to run.
/// </summary>
/// <remarks>
/// Foundational slice: only the default-code <c>self_verify</c> prefix (optionally preceded by an
/// <c>expiry_verify</c> frame) is resolved natively. Anything else yields
/// <see cref="FrameTxPayerOutcome.RequiresSimulation"/> and is left to a later simulation layer — a
/// deployed/delegated sender, a deploy-factory frame, or an <c>only_verify | pay</c> prefix naming a
/// third-party payer (an EOA sponsor or a canonical/non-canonical paymaster) the pool cannot yet
/// authenticate. A prefix with no payment-approving frame at all is a structural
/// <see cref="FrameTxPayerOutcome.NoPayer"/>. Native resolution mirrors the execution loop's
/// default-code approval conditions for the legible prefix (<c>TransactionProcessorBase.FrameTx.cs</c>);
/// it identifies who the structural payer is, not that execution will set one. Solvency/exposure,
/// the <c>MAX_VERIFY_GAS</c> budget, and cryptographic-signature verification are separate deferred
/// gates (ethereum/EIPs#12007); until the signature gate lands a third-party payer is never named
/// natively (the <c>only_verify | pay</c> prefix yields <see cref="FrameTxPayerOutcome.RequiresSimulation"/>),
/// so a forged signature cannot resolve to an arbitrary victim.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </remarks>
internal static class FrameTxPayerResolver
{
    /// <summary>
    /// Resolves the payer of <paramref name="tx"/> against <paramref name="state"/> (the chain head).
    /// </summary>
    /// <param name="senderAccount">The sender's chain-head account, already fetched by the caller.</param>
    /// <returns>The payer outcome and the captured state dependency set.</returns>
    public static FrameTxPayerResolution Resolve(Transaction tx, IReadOnlyStateProvider state, in AccountStruct senderAccount)
    {
        TxFrame[]? frames = tx.Frames;
        Address? sender = tx.SenderAddress;
        if (frames is null || frames.Length == 0 || sender is null)
        {
            return new FrameTxPayerResolution(FrameTxPayerOutcome.RequiresSimulation, null, default);
        }

        TxFrameSignature[] signatures = tx.FrameSignatures ?? [];

        // A never-seen account is the zeroed struct, whose code hash is not the empty-keccak, so
        // normalize both the existence flag and the hash here.
        bool senderExists = !senderAccount.IsNull;
        bool senderHasCode = senderExists && senderAccount.HasCode;
        ValueHash256 senderCodeHash = senderExists ? senderAccount.CodeHash : Keccak.OfAnEmptyString.ValueHash256;
        // Copied out of the in-parameter so the dependency-set builders below (local functions) can close over it.
        ulong senderNonce = senderAccount.Nonce;

        // Optional leading expiry_verify frame: skipped for shape matching (it does not set the
        // payer), but its deadline and the EXPIRY_VERIFIER code join the dependency set (γρ.795).
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
            // Legible only for a default-code sender; a deployed or EIP-7702-delegated sender runs
            // its own account code and must be simulated.
            if (senderHasCode)
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            // Execution's default code approves only when index-0 is a canonical-hash secp256k1
            // signature resolving to the sender. A non-matching signature shape is not a native proof
            // of invalidity — the hoisted-list vs VERIFY-frame-data signature placement is an open
            // cross-client question (TransactionProcessorBase.FrameTx.cs) — so defer the verdict to
            // execution rather than dropping the tx at admission.
            return DefaultCodeApproves(signatures, sender)
                ? ResolvedTo(sender, in senderCodeHash, senderAccount.Balance)
                : Unresolved(FrameTxPayerOutcome.RequiresSimulation);
        }

        // Canonical paymaster relay: only_verify approves execution but not payment; a following pay
        // frame sets the payer.
        if (IsOnlyVerify(verifyFrame, sender))
        {
            // A lone only_verify frame has no payment-approving frame after it, so it provably never sets
            // a payer regardless of signature or sender code (APPROVE cannot exceed the frame's allowed
            // scope) — a structural NoPayer.
            if (index + 1 >= frames.Length)
            {
                return Unresolved(FrameTxPayerOutcome.NoPayer);
            }

            // A following pay frame names a third-party payer the pool cannot authenticate natively (its
            // pay-frame signature is unverified at admission), and a deployed/delegated sender runs its
            // own code; both are deferred to the signature-verification / simulation layer.
            return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
        }

        // Not a recognized legible prefix (e.g. a deploy frame, or an unrecognized VERIFY shape).
        return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
    }

    /// <summary>
    /// Computes the EIP-8141 validation-prefix verification gas of <paramref name="tx"/> — the sum of
    /// <c>gas_limit</c> across the validation-prefix frames plus the intrinsic cost of validating
    /// <c>tx.signatures</c> — which admission bounds by <see cref="Eip8141Constants.MaxVerifyGas"/>.
    /// </summary>
    /// <remarks>
    /// State-free structural analysis: the prefix span depends only on frame shapes, so it applies to
    /// the recognized shapes whether or not the payer resolves natively. This is the Direct Evaluation
    /// form of the bound (ethereum/EIPs#12007 "Direct evaluation MUST apply the same limits as
    /// simulation"); no EVM execution is needed. Returns <c>false</c> when the prefix is not one of the
    /// recognized shapes (bound not computable here; structural-match gate deferred). Arithmetic
    /// overflow is reported as <see cref="ulong.MaxValue"/> so the caller treats it as over budget.
    /// <c>deploy</c>-prefixed shapes are not analyzed here (deferred with the simulation layer).
    /// </remarks>
    /// <returns><c>true</c> with <paramref name="verifyGas"/> set for a recognized prefix; otherwise <c>false</c>.</returns>
    public static bool TryGetValidationPrefixVerifyGas(Transaction tx, out ulong verifyGas)
    {
        verifyGas = 0;
        TxFrame[]? frames = tx.Frames;
        Address? sender = tx.SenderAddress;
        if (frames is null || frames.Length == 0 || sender is null)
        {
            return false;
        }

        // A leading expiry_verify frame is skipped for shape matching but counts toward the prefix gas.
        int index = IsExpiryVerifyFrame(frames[0]) ? 1 : 0;
        if (index >= frames.Length)
        {
            return false;
        }

        int prefixLength;
        TxFrame verifyFrame = frames[index];
        if (IsSelfVerify(verifyFrame, sender))
        {
            prefixLength = index + 1;
        }
        else if (IsOnlyVerify(verifyFrame, sender) && index + 1 < frames.Length && IsPay(frames[index + 1]))
        {
            prefixLength = index + 2;
        }
        else
        {
            return false;
        }

        ulong prefixGas = 0;
        for (int i = 0; i < prefixLength; i++)
        {
            ulong next = prefixGas + frames[i].GasLimit;
            if (next < prefixGas)
            {
                verifyGas = ulong.MaxValue;
                return true;
            }
            prefixGas = next;
        }

        ulong signatureCost = 0;
        foreach (TxFrameSignature signature in tx.FrameSignatures ?? [])
        {
            signatureCost += Eip8141Constants.SignatureVerificationGasCost(signature.Scheme);
        }

        ulong total = prefixGas + signatureCost;
        verifyGas = total < prefixGas ? ulong.MaxValue : total;
        return true;
    }

    /// <summary>
    /// Mirrors the execution default-code VERIFY approval check for a self_verify prefix: an index-0
    /// canonical-hash (empty <c>msg</c>) secp256k1 signature whose resolved signer is the sender.
    /// Cryptographic verification is a separate deferred gate; this checks only the structural conditions.
    /// </summary>
    /// <remarks>
    /// Only the index-0 / sender case is modelled; a payment-only verifier reads index 1 to name a
    /// third party and is deferred to simulation until frame signatures are verified at admission.
    /// </remarks>
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
