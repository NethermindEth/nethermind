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
/// Foundational slice: only the default-code <c>self_verify</c> and <c>only_verify | pay</c> prefixes
/// (optionally preceded by an <c>expiry_verify</c> frame) are resolved natively; anything reaching
/// deployed code — a deployed/delegated sender, a deploy-factory frame, a canonical (code hash
/// unpinned) or non-canonical paymaster — yields <see cref="FrameTxPayerOutcome.RequiresSimulation"/>
/// and is left to a later simulation layer. Native resolution mirrors the execution loop's
/// default-code approval conditions for the legible prefixes (<c>TransactionProcessorBase.FrameTx.cs</c>);
/// it identifies who the structural payer is, not that execution will set one. Solvency/exposure,
/// the <c>MAX_VERIFY_GAS</c> budget, and cryptographic-signature verification are separate deferred
/// gates (ethereum/EIPs#12007); until the signature gate lands a third-party payer is never named
/// natively (see the sponsored branch), so a forged signature cannot resolve to an arbitrary victim.
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
            // signature resolving to the sender; otherwise the VERIFY frame reverts (invalid tx).
            return DefaultCodeApproves(signatures, sigIndex: 0, expectedSigner: sender, sender)
                ? ResolvedTo(sender, in senderCodeHash, senderAccount.Balance)
                : Unresolved(FrameTxPayerOutcome.NoPayer);
        }

        // Canonical paymaster relay: only_verify approves the sender, then a pay frame sets the payer.
        if (IsOnlyVerify(verifyFrame, sender))
        {
            if (senderHasCode)
            {
                return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
            }

            if (!DefaultCodeApproves(signatures, sigIndex: 0, expectedSigner: sender, sender))
            {
                return Unresolved(FrameTxPayerOutcome.NoPayer);
            }

            // only_verify approves execution but not payment; a pay frame must follow to set the payer.
            if (index + 1 >= frames.Length || !IsPay(frames[index + 1]))
            {
                return Unresolved(index + 1 < frames.Length
                    ? FrameTxPayerOutcome.RequiresSimulation
                    : FrameTxPayerOutcome.NoPayer);
            }

            // The pay frame names a third party (an EOA sponsor or a paymaster) as the payer, and its
            // pay-frame signature is not cryptographically verified at admission. Naming that payer on
            // an unverified signature is a griefing vector — a forged signature could charge an
            // arbitrary victim's exposure budget — so it is deferred to the signature-verification /
            // simulation layer rather than resolved natively (ethereum/EIPs#12007).
            return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
        }

        // Not a recognized legible prefix (e.g. a deploy frame, or an unrecognized VERIFY shape).
        return Unresolved(FrameTxPayerOutcome.RequiresSimulation);
    }

    /// <summary>
    /// Mirrors the execution default-code VERIFY approval check: a canonical-hash (empty <c>msg</c>)
    /// secp256k1 signature at <paramref name="sigIndex"/> whose resolved signer is <paramref name="expectedSigner"/>.
    /// Cryptographic verification is a separate deferred gate; this checks only the structural conditions.
    /// </summary>
    private static bool DefaultCodeApproves(TxFrameSignature[] signatures, int sigIndex, Address expectedSigner, Address sender)
    {
        if (signatures.Length <= sigIndex)
        {
            return false;
        }

        TxFrameSignature signature = signatures[sigIndex];
        return signature.Scheme == TxFrameSignature.SchemeSecp256k1
               && signature.Msg.IsEmpty
               && (signature.Signer ?? sender) == expectedSigner;
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

    private static bool IsPay(TxFrame frame) =>
        frame.Mode == TxFrame.ModeVerify
        && frame.Flags == TxFrame.ApprovePayment;
}
