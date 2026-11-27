// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Ethereum.Test.Base;

/// <summary>
/// Client message fragments behind each EIP-8141 <c>TransactionException.TYPE_6_*</c> fixture label.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Format"/>, <see cref="Signature"/> and <see cref="Execution"/> must stay pairwise disjoint
/// under substring matching: a fixture naming one label has to fail when the client rejects for one of the
/// other two, so no set may use a fragment broad enough to catch another's messages — in particular not a
/// bare "frame", which would catch the execution failure "validation prefix frame reverted". Constants are
/// referenced rather than copied wherever one exists.
/// </para>
/// <para>
/// <see cref="Decode"/> is deliberately outside that invariant. Most of its fragments are generic RLP-decoder
/// text carrying no frame context, so they also match decode failures from unrelated payloads. That only widens
/// what satisfies <c>TYPE_6_INVALID_FRAME_FORMAT</c> — the exception table is additive and a fixture passes
/// when its expected label is among those matched, so a broad fragment can mask a rejection for the wrong
/// reason but can never turn a passing lane red. Narrowing it needs frame context in the decoder messages
/// themselves.
/// </para>
/// <para>
/// <c>BlockchainTestBase</c> maps all three labels. <c>TransactionTestBase</c> maps the first two: it runs
/// <c>TxValidator.IsWellFormed</c> and then <c>FrameTxClaimVerifier</c>, the pair a transaction must
/// clear to be accepted off the wire, but never the transaction processor, so no <see cref="Execution"/>
/// message can reach it.
/// </para>
/// </remarks>
public static class FrameExceptionFragments
{
    /// <summary>Static frame-shape rules — the spec "Constraints" block, plus the decode-time shape checks.</summary>
    public static readonly string[] Format =
    [
        FrameTxValidation.MissingFrames,
        FrameTxValidation.MissingSender,
        FrameTxValidation.InvalidMode,
        FrameTxValidation.AssertFrameNotTrailing,
        FrameTxValidation.AssertFrameNotEnabled,
        FrameTxValidation.InvalidFlags,
        FrameTxValidation.ValueOutsideSenderMode,
        FrameTxValidation.ExecutionApprovalWrongTarget,
        FrameTxValidation.AtomicBatchOnLastFrame,
        FrameTxValidation.AtomicBatchOnVerifyFrame,
        FrameTxValidation.AtomicBatchOnAssertFrame,
        FrameTxValidation.AtomicBatchFollowedByVerifyFrame,
        FrameTxValidation.AtomicBatchFollowedByAssertFrame,
        FrameTxValidation.ApprovalScopeInAtomicBatch,
        FrameTxValidation.FrameGasOverflow,
        FrameTxValidation.InvalidExpiryFrame,
        FrameTxValidation.MultipleExpiryFrames,
        FrameTxValidation.TooManyClaims,
        FrameTxValidation.InvalidClaimKey,
        FrameTxValidation.ClaimKeyNotAnAddress,
        FrameTxValidation.InvalidMsgLength,
        FrameTxValidation.ZeroDigestMsg,
        FrameTxValidation.KeyedNoncesNotEnabled,
        FrameTxValidation.LegacyNonceNotAllowed,
        FrameTxValidation.MalformedNonceKeySet,
        FrameTxValidation.ProtocolClaimWithGasOrWitnessLength,
        FrameTxValidation.CustomClaimWithoutGas,
        FrameTxValidation.ClaimWitnessTooLong,
        FrameTxValidation.ClaimGasOverflow,
        "frame transaction must not carry a trailing element",
    ];

    /// <summary>Claim verification against the segregated witnesses.</summary>
    public static readonly string[] Signature =
    [
        FrameTxClaimVerifier.InvalidClaim,
        FrameTxClaimVerifier.MissingWitnesses,
        FrameTxClaimVerifier.WitnessCountMismatch,
        FrameTxClaimVerifier.InvalidWitnessLength,
        FrameTxClaimVerifier.P256NotSupported,
        // A claim the block's dependency proof cannot satisfy, which the fixtures derived from state tests name as
        // their transaction's invalid claim.
        FrameTxDependencyProof.WitnessLengthMismatch,
        FrameTxDependencyProof.VerifierNotDeployed,
    ];

    /// <summary>
    /// Rejections of a block's dependency proof, labelled <c>BlockException.INVALID_DEPENDENCY_PROOF</c>. Outside the
    /// pairwise-disjoint invariant, like <see cref="Decode"/>: these are block-level, and a witness the proof carries
    /// that does not verify surfaces from the transaction it is assigned to.
    /// </summary>
    public static readonly string[] DependencyProof =
    [
        FrameTxDependencyProof.TooLarge,
        FrameTxDependencyProof.Malformed,
        FrameTxDependencyProof.WitnessCountMismatch,
        FrameTxDependencyProof.WitnessLengthMismatch,
        FrameTxDependencyProof.VerifierNotDeployed,
        FrameTxDependencyProof.VerifierGasExceedsBlockGasLimit,
        FrameTxClaimVerifier.InvalidClaim,
    ];

    /// <summary>
    /// A well-formed, correctly signed transaction that a frame then halts. No constants to reference:
    /// these are inline details in the frame transaction processor.
    /// </summary>
    public static readonly string[] Execution =
    [
        // Covers both the VERIFY and the validation-prefix wording of a reverting frame.
        "frame reverted",
        "SENDER frame before execution approval",
        "never set a payer",
    ];

    /// <summary>
    /// A fee field wider than its type, which the decoder rejects through its length guard.
    /// </summary>
    /// <remarks>
    /// The guard names neither the field nor the type, so these widen both fee labels suite-wide to
    /// any untyped limit rejection, not merely to the other fee field. Both wordings are one
    /// rejection: <c>Rlp.ThrowCountOverLimit</c> composes the detailed text only when <c>Rlp</c>'s
    /// static logger has trace enabled and otherwise throws the bare message, so listing only the
    /// detailed one leaves the mapping dead in the default configuration.
    /// </remarks>
    public static readonly string[] FeeOverflow =
    [
        "Collection count",
        "An RLP limit exceeded",
    ];

    /// <summary>
    /// Decode-time rejections of a frame transaction's encoding — a field too wide or too long for its type, a field
    /// not encoded canonically, or a list whose header does not match its elements — which the fixtures also name as
    /// format failures. Reported before any rule runs, so they name no rule.
    /// </summary>
    public static readonly string[] Decode =
    [
        "Unexpected length of integer value",
        // A nonce or nonce_seq wider than 64 bits, as BaseTxDecoder.DecodeNonce reports it. EIP-8250's
        // seq_beyond_width labels it TYPE_6_INVALID_FRAME_FORMAT only; a plain nonce also matches NONCE_OVERFLOW.
        "NonceTooWide",
        // Two producers: RlpReader words a bad address prefix, RlpHelpers a field that should be a
        // sequence and is not. The latter is trimmed of the byte range it goes on to name.
        "Unexpected RLP prefix",
        "Expected a sequence prefix",
        "frame transaction payload is incomplete",
        // A field not encoded canonically: an integer or one-byte string with a leading zero or a header it does
        // not need, a one-byte field (mode, flags) under a string or list prefix, and a long-form length on a string
        // shorter than 56 bytes.
        "Non-canonical integer",
        "Unexpected byte value",
        "Expected length greater than or equal to 56",
        // A list header that does not match the bytes its elements take up, e.g. a frame's limits missing one.
        "Data checkpoint failed",
        // A fees list without both max_fee and max_priority_fee_per_gas.
        "fees must hold two items",
        // Kept in step with FeeOverflow by DecodeCarriesEveryFeeOverflowWording, rather than spread
        // from it: a static initialiser reading a field declared below it silently reads null.
        "Collection count",
        "An RLP limit exceeded",
    ];
}
