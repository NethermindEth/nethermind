// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Ethereum.Test.Base;

/// <summary>
/// Client message fragments behind each EIP-8141 <c>TransactionException.TYPE_6_*</c> fixture label,
/// shared by the transaction-test and blockchain-test exception tables so the two cannot drift.
/// </summary>
/// <remarks>
/// The three sets must stay pairwise disjoint under substring matching: a fixture naming one label
/// has to fail when the client rejects for one of the other two, so no set may use a fragment broad
/// enough to catch another's messages — in particular not a bare "frame". Constants are referenced
/// rather than copied wherever one exists.
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
        // The fixtures file a signer that does not match as a format failure, and a signature that
        // does not verify as a signature failure.
        FrameTxSignatureValidator.InvalidSecp256k1Signer,
        FrameTxSignatureValidator.InvalidP256Signer,
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
        "VERIFY frame reverted",
        "SENDER frame before execution approval",
        "never set a payer",
    ];

    /// <summary>
    /// Decode-time rejections of a frame field too wide or too long for its type, which the fixtures
    /// also name as format failures. Reported before any rule runs, so they name no rule.
    /// </summary>
    public static readonly string[] Decode =
    [
        "Unexpected length of integer value",
        "Unexpected RLP prefix",
        "Collection count",
    ];
}
