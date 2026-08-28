// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
/// <see cref="Decode"/> is deliberately outside that invariant. Its fragments are generic RLP-decoder text
/// carrying no frame context, so they also match decode failures from unrelated payloads. That only widens
/// what satisfies <c>TYPE_6_INVALID_FRAME_FORMAT</c> — the exception table is additive and a fixture passes
/// when its expected label is among those matched, so a broad fragment can mask a rejection for the wrong
/// reason but can never turn a passing lane red. Narrowing it needs frame context in the decoder messages
/// themselves.
/// </para>
/// <para>
/// <c>BlockchainTestBase</c> maps all three labels. <c>TransactionTestBase</c> maps only
/// <c>TYPE_6_INVALID_FRAME_FORMAT</c>, from <see cref="Format"/> and <see cref="Decode"/>: it stops at
/// <c>TxValidator.IsWellFormed</c>, which runs neither the signature validator nor the transaction
/// processor, so no <see cref="Signature"/> or <see cref="Execution"/> message can reach it.
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
        FrameTxValidation.PostTxNotTrailing,
        FrameTxValidation.PostTxNotEnabled,
        FrameTxValidation.InvalidFlags,
        FrameTxValidation.ValueOutsideSenderMode,
        FrameTxValidation.ExecutionApprovalWrongTarget,
        FrameTxValidation.AtomicBatchOnLastFrame,
        FrameTxValidation.AtomicBatchOnVerifyFrame,
        FrameTxValidation.AtomicBatchOnPostTxFrame,
        FrameTxValidation.AtomicBatchFollowedByVerifyFrame,
        FrameTxValidation.AtomicBatchFollowedByPostTxFrame,
        FrameTxValidation.ApprovalScopeInAtomicBatch,
        FrameTxValidation.FrameGasOverflow,
        FrameTxValidation.InvalidExpiryFrame,
        FrameTxValidation.MultipleExpiryFrames,
        FrameTxValidation.InvalidSignatureScheme,
        FrameTxValidation.ArbitrarySignatureWithSigner,
        // A suffix of the decoder's and the signature validator's wordings of the same rule, so this
        // one fragment covers all three places a bad msg length is caught.
        FrameTxValidation.InvalidMsgLength,
        FrameTxValidation.ZeroDigestMsg,
        FrameTxValidation.BlobFeeWithoutBlobs,
        FrameTxValidation.KeyedNoncesNotEnabled,
        FrameTxValidation.LegacyNonceNotAllowed,
        FrameTxValidation.MalformedNonceKeySet,
        FrameTxValidation.TooManyRecentRootReferences,
        FrameTxValidation.RecentRootReferencesNotEnabled,
        // The fixtures file a signer that does not match as a format failure, and a signature that
        // does not verify as a signature failure.
        FrameTxSignatureValidator.InvalidSecp256k1Signer,
        FrameTxSignatureValidator.InvalidP256Signer,
        // A decoder literal, no constant to reference: the trailing element is present but is not
        // the recent-root-reference sequence. Thrown before any rule runs, so no rule names it.
        "frame transaction must not carry a trailing signature",
    ];

    /// <summary>Signature verification — the spec <c>validate_signature</c> step.</summary>
    public static readonly string[] Signature =
    [
        FrameTxSignatureValidator.InvalidSignature,
        FrameTxSignatureValidator.InvalidSignatureLength,
        FrameTxSignatureValidator.NonCanonicalSignature,
        FrameTxSignatureValidator.NonCanonicalP256Signature,
        FrameTxSignatureValidator.P256NotSupported,
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
    /// Decode-time rejections of a frame field too wide or too long for its type, which the fixtures
    /// also name as format failures. Reported before any rule runs, so they name no rule.
    /// </summary>
    public static readonly string[] Decode =
    [
        "Unexpected length of integer value",
        // Two distinct rejections: "Unexpected RLP prefix" is the address-decode wording, and a
        // field that should be a sequence and is not reports its own, trimmed of the range here.
        "Unexpected RLP prefix",
        "Expected a sequence prefix",
        .. FeeOverflow,
    ];
}
