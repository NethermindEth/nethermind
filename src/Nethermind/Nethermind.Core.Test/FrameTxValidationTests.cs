// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>
/// One case per assert of the EIP-8141 "Constraints" block (plus the expiry verifier frame
/// static rules), so every spec constraint is pinned by name. Checks that are Nethermind
/// interpretations rather than literal spec text carry an EIP8141- note on their case.
/// </summary>
public class FrameTxValidationTests
{
    [TestCaseSource(nameof(ConstraintCases))]
    public void IsWellFormed_SpecConstraintMatrix_ReturnsExpectedError(Action<Transaction> mutate, string? expectedError)
    {
        Transaction tx = CreateValidFrameTx(mutate);

        bool wellFormed = FrameTxValidation.IsWellFormed(tx, out string? error);

        Assert.That(wellFormed, Is.EqualTo(expectedError is null));
        Assert.That(error, Is.EqualTo(expectedError));
    }

    private static IEnumerable<TestCaseData> ConstraintCases()
    {
        yield return Case("MinimalSelfVerifyFrame_Valid",
            static tx => { }, null);

        // assert len(tx.frames) > 0 and len(tx.frames) <= MAX_FRAMES
        yield return Case("NullFrames_MissingFrames",
            static tx => tx.Frames = null, FrameTxValidation.MissingFrames);
        yield return Case("EmptyFrames_MissingFrames",
            static tx => tx.Frames = [], FrameTxValidation.MissingFrames);
        yield return Case("MaxFramesCount_Valid",
            static tx => tx.Frames = Enumerable.Repeat(0, Eip8141Constants.MaxFrames).Select(_ => DefaultModeFrame()).ToArray(), null);
        yield return Case("MaxFramesExceeded_MissingFrames",
            static tx => tx.Frames = Enumerable.Repeat(0, Eip8141Constants.MaxFrames + 1).Select(_ => DefaultModeFrame()).ToArray(),
            FrameTxValidation.MissingFrames);

        // assert len(tx.sender) == 20 — a null sender is the decoded-form equivalent.
        yield return Case("NullSender_MissingSender",
            static tx => tx.SenderAddress = null, FrameTxValidation.MissingSender);

        // assert frame.mode < 3
        yield return Case("FrameModeThree_InvalidMode",
            static tx => tx.Frames = [Frame(mode: 3)], FrameTxValidation.InvalidMode);

        // assert frame.flags < 8
        yield return Case("FrameFlagsEight_InvalidFlags",
            static tx => tx.Frames = [Frame(flags: 8)], FrameTxValidation.InvalidFlags);

        // assert frame.mode == SENDER or frame.value == 0
        yield return Case("ValueOnDefaultFrame_ValueOutsideSenderMode",
            static tx => tx.Frames = [Frame(mode: TxFrame.ModeDefault, value: UInt256.One)],
            FrameTxValidation.ValueOutsideSenderMode);
        yield return Case("ValueOnVerifyFrame_ValueOutsideSenderMode",
            static tx => tx.Frames = [Frame(mode: TxFrame.ModeVerify, value: UInt256.One)],
            FrameTxValidation.ValueOutsideSenderMode);
        yield return Case("ValueOnSenderFrame_Valid",
            static tx => tx.Frames = [SelfVerifyFrame(), Frame(mode: TxFrame.ModeSender, target: TestItem.AddressB, value: UInt256.One)],
            null);

        // if frame.flags & APPROVE_EXECUTION: assert frame.target is None or frame.target == tx.sender
        yield return Case("ExecutionApprovalTargetsThirdParty_ExecutionApprovalWrongTarget",
            static tx => tx.Frames = [Frame(mode: TxFrame.ModeVerify, flags: TxFrame.ApproveExecution, target: TestItem.AddressB)],
            FrameTxValidation.ExecutionApprovalWrongTarget);
        yield return Case("ExecutionApprovalTargetsSenderExplicitly_Valid",
            static tx => tx.Frames = [Frame(mode: TxFrame.ModeVerify, flags: TxFrame.ApproveExecution, target: TestItem.AddressA)],
            null);

        // if frame.flags & ATOMIC_BATCH_FLAG: assert i + 1 < len(tx.frames)
        yield return Case("AtomicBatchFlagOnLastFrame_AtomicBatchOnLastFrame",
            static tx => tx.Frames = [SelfVerifyFrame(), Frame(flags: TxFrame.AtomicBatchFlag)],
            FrameTxValidation.AtomicBatchOnLastFrame);
        yield return Case("AtomicBatchFlagOnInnerFrame_Valid",
            static tx => tx.Frames = [SelfVerifyFrame(), Frame(flags: TxFrame.AtomicBatchFlag), DefaultModeFrame()],
            null);

        // ethereum/EIPs#11955: atomic batches contain only non-VERIFY frames.
        yield return Case("AtomicBatchFlagOnVerifyFrame_AtomicBatchOnVerifyFrame",
            static tx => tx.Frames = [Frame(mode: TxFrame.ModeVerify, flags: TxFrame.AtomicBatchFlag), DefaultModeFrame()],
            FrameTxValidation.AtomicBatchOnVerifyFrame);
        yield return Case("AtomicBatchFollowedByVerifyFrame_AtomicBatchFollowedByVerifyFrame",
            static tx => tx.Frames = [SelfVerifyFrame(), Frame(flags: TxFrame.AtomicBatchFlag), Frame(mode: TxFrame.ModeVerify)],
            FrameTxValidation.AtomicBatchFollowedByVerifyFrame);

        // EIP-8141: a frame belonging to an atomic batch (flagged, or the terminating frame following
        // a flagged one) must not carry approval scope.
        yield return Case("ApprovePaymentOnBatchFrame_ApprovalScopeInAtomicBatch",
            static tx => tx.Frames = [SelfVerifyFrame(), Frame(flags: (byte)(TxFrame.ApprovePayment | TxFrame.AtomicBatchFlag)), DefaultModeFrame()],
            FrameTxValidation.ApprovalScopeInAtomicBatch);
        yield return Case("ApproveExecutionOnBatchFrame_ApprovalScopeInAtomicBatch",
            static tx => tx.Frames = [SelfVerifyFrame(), Frame(flags: (byte)(TxFrame.ApproveExecution | TxFrame.AtomicBatchFlag)), DefaultModeFrame()],
            FrameTxValidation.ApprovalScopeInAtomicBatch);
        yield return Case("ApprovalOnBatchTerminatingFrame_ApprovalScopeInAtomicBatch",
            static tx => tx.Frames = [SelfVerifyFrame(), Frame(flags: TxFrame.AtomicBatchFlag), Frame(flags: TxFrame.ApprovePayment)],
            FrameTxValidation.ApprovalScopeInAtomicBatch);
        yield return Case("BatchWithoutApprovalScope_Valid",
            static tx => tx.Frames = [SelfVerifyFrame(), Frame(flags: TxFrame.AtomicBatchFlag), DefaultModeFrame()],
            null);

        // total_frame_gas accumulated across frames must not overflow 2^64 - 1
        yield return Case("TotalFrameGasOverflows_FrameGasOverflow",
            static tx => tx.Frames = [Frame(gasLimit: ulong.MaxValue), Frame(gasLimit: 1)],
            FrameTxValidation.FrameGasOverflow);

        // Signature scheme must be ARBITRARY, SECP256K1, or P256.
        yield return Case("UnknownSignatureScheme_InvalidSignatureScheme",
            static tx => tx.FrameSignatures = [new TxFrameSignature(3, null, default, default)],
            FrameTxValidation.InvalidSignatureScheme);
        yield return Case("Secp256k1SignatureWithExplicitSigner_Valid",
            static tx => tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressB, default, new byte[65])],
            null);

        // For ARBITRARY, signer MUST be empty.
        yield return Case("ArbitrarySignatureNamesSigner_ArbitrarySignatureWithSigner",
            static tx => tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeArbitrary, TestItem.AddressB, default, new byte[1])],
            FrameTxValidation.ArbitrarySignatureWithSigner);

        // msg is empty (canonical hash) or an explicit non-zero 32-byte digest.
        yield return Case("SignatureMsgSixteenBytes_InvalidMsgLength",
            static tx => tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, new byte[16], new byte[65])],
            FrameTxValidation.InvalidMsgLength);
        yield return Case("SignatureMsgZeroDigest_ZeroDigestMsg",
            static tx => tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, new byte[32], new byte[65])],
            FrameTxValidation.ZeroDigestMsg);
        yield return Case("SignatureMsgNonZeroDigest_Valid",
            static tx => tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, NonZeroDigest(), new byte[65])],
            null);

        // Spec-backed via the max_fee_per_blob_gas field description ("must be 0 when
        // blob_versioned_hashes is empty") — the rule just is not in the Constraints block.
        // EIP8141-ISSUE: propose moving it into Constraints upstream.
        yield return Case("BlobFeeWithoutBlobHashes_BlobFeeWithoutBlobs",
            static tx => tx.MaxFeePerBlobGas = UInt256.One, FrameTxValidation.BlobFeeWithoutBlobs);

        yield return Case("BlobHashesWithBlobFee_Valid",
            static tx => { tx.BlobVersionedHashes = [new byte[32]]; tx.MaxFeePerBlobGas = UInt256.One; }, null);
        // Expiry verifier frame: flags == 0, value == 0, len(data) == EXPIRY_DATA_LENGTH, at most one.
        yield return Case("ExpiryFrameWellFormed_Valid",
            static tx => tx.Frames = [SelfVerifyFrame(), ExpiryFrame()], null);
        yield return Case("ExpiryFrameWithFlags_InvalidExpiryFrame",
            static tx => tx.Frames = [Frame(mode: TxFrame.ModeVerify, flags: TxFrame.ApprovePayment, target: Eip8141Constants.ExpiryVerifierAddress, data: new byte[Eip8141Constants.ExpiryDataLength])],
            FrameTxValidation.InvalidExpiryFrame);
        yield return Case("ExpiryFrameWithShortData_InvalidExpiryFrame",
            static tx => tx.Frames = [Frame(mode: TxFrame.ModeVerify, target: Eip8141Constants.ExpiryVerifierAddress, data: new byte[Eip8141Constants.ExpiryDataLength - 1])],
            FrameTxValidation.InvalidExpiryFrame);
        yield return Case("TwoExpiryFrames_MultipleExpiryFrames",
            static tx => tx.Frames = [SelfVerifyFrame(), ExpiryFrame(), ExpiryFrame()],
            FrameTxValidation.MultipleExpiryFrames);
        // Only VERIFY-mode frames targeting the verifier are expiry frames; a DEFAULT-mode call
        // to the same address is an ordinary frame.
        yield return Case("DefaultModeCallToExpiryVerifier_Valid",
            static tx => tx.Frames = [Frame(mode: TxFrame.ModeDefault, target: Eip8141Constants.ExpiryVerifierAddress, data: new byte[3])],
            null);
    }

    private static TestCaseData Case(string name, Action<Transaction> mutate, string? expectedError) =>
        new TestCaseData(mutate, expectedError).SetName($"IsWellFormed_{name}");

    private static Transaction CreateValidFrameTx(Action<Transaction> mutate)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [SelfVerifyFrame()],
            FrameSignatures = [],
        };
        mutate(tx);
        return tx;
    }

    private static TxFrame SelfVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default);

    private static TxFrame DefaultModeFrame() => Frame();

    private static TxFrame ExpiryFrame() =>
        new(TxFrame.ModeVerify, flags: 0, Eip8141Constants.ExpiryVerifierAddress, gasLimit: 30_000, UInt256.Zero, new byte[Eip8141Constants.ExpiryDataLength]);

    private static TxFrame Frame(byte mode = TxFrame.ModeDefault, byte flags = 0, Address? target = null, ulong gasLimit = 50_000, UInt256 value = default, byte[]? data = null) =>
        new(mode, flags, target, gasLimit, value, data ?? Array.Empty<byte>());

    private static byte[] NonZeroDigest()
    {
        byte[] digest = new byte[32];
        digest[31] = 1;
        return digest;
    }

    private static IEnumerable<TestCaseData> ValidationWorkCases()
    {
        static TestCaseData Work(string name, TxFrame[] frames, TxFrameSignature[] signatures, ulong expected) =>
            new TestCaseData(frames, signatures, expected).SetName($"ValidationWorkGas_{name}");

        static TxFrameSignature Signature(byte scheme) => new(scheme, null, default, Array.Empty<byte>());

        static TxFrame OnlyVerifyFrame() =>
            Frame(TxFrame.ModeVerify, TxFrame.ApproveExecution, gasLimit: 40_000);

        static TxFrame PayFrame() =>
            Frame(TxFrame.ModeVerify, TxFrame.ApprovePayment, TestItem.AddressB, gasLimit: 30_000);

        // The prefix ends at the self-verify frame; the execution frame behind it is paid for out of
        // the transaction's own gas and is outside the budget.
        yield return Work("SelfVerify_CountsOnlyThePrefix",
            [SelfVerifyFrame(), Frame(TxFrame.ModeSender, target: TestItem.AddressB, gasLimit: 5_000_000)],
            [], 100_000);

        // The expiry verifier frame is skipped when matching the shape, but its work is still the
        // node's to do before any gas is paid.
        yield return Work("ExpiryThenSelfVerify_CountsBoth",
            [ExpiryFrame(), SelfVerifyFrame()], [], 130_000);

        yield return Work("DeployThenSelfVerify_CountsBoth",
            [DefaultModeFrame(), SelfVerifyFrame()], [], 150_000);

        yield return Work("OnlyVerifyThenPay_CountsBothPrefixFrames",
            [OnlyVerifyFrame(), PayFrame(), Frame(TxFrame.ModeSender, target: TestItem.AddressC, gasLimit: 900_000)],
            [], 70_000);

        yield return Work("DeployThenOnlyVerifyThenPay_CountsAllThree",
            [DefaultModeFrame(), OnlyVerifyFrame(), PayFrame()], [], 120_000);

        yield return Work("SignatureCostsCountAgainstTheBudget",
            [SelfVerifyFrame()],
            [Signature(TxFrameSignature.SchemeSecp256k1), Signature(TxFrameSignature.SchemeP256)],
            100_000 + Eip8141Constants.Secp256k1VerificationGasCost + Eip8141Constants.P256VerificationGasCost);

        yield return Work("OverflowingPrefixGas_Saturates",
            [DefaultModeFrame(), Frame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, gasLimit: ulong.MaxValue)],
            [], ulong.MaxValue);

        // Approving flags on a DEFAULT frame do not end a prefix: whether the target approves at all
        // depends on code the sender controls, so the frames behind it may still run unpaid and the
        // whole list is charged. Estimating this layout at its first frame is what let a sender on a
        // delegation walk past the ceiling.
        yield return Work("DefaultFrameWithApprovingFlags_ChargesTheWholeList",
            [Frame(flags: TxFrame.ApproveExecutionAndPayment, gasLimit: 1_000), Frame(gasLimit: 3_000_000)],
            [], 3_001_000);

        // APPROVE rejects a payment approval that no execution approval precedes.
        yield return Work("PayBeforeOnlyVerify_ChargesTheWholeList",
            [PayFrame(), OnlyVerifyFrame()], [], 70_000);

        // A verifier the sender does not control is third-party mutable state.
        yield return Work("SelfVerifyTargetingAThirdParty_ChargesTheWholeList",
            [Frame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, TestItem.AddressB), Frame(gasLimit: 900_000)],
            [], 950_000);

        yield return Work("PrefixFrameInAnAtomicBatch_ChargesTheWholeList",
            [Frame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment | TxFrame.AtomicBatchFlag), Frame(gasLimit: 900_000)],
            [], 950_000);

        yield return Work("OnlyVerifyWithoutPay_ChargesTheWholeList",
            [OnlyVerifyFrame(), Frame(TxFrame.ModeSender, gasLimit: 900_000)], [], 940_000);

        yield return Work("NoApprovingFrameAtAll_ChargesTheWholeList",
            [Frame(gasLimit: 10_000), Frame(gasLimit: 20_000)], [], 30_000);
    }

    [TestCaseSource(nameof(ValidationWorkCases))]
    public void ValidationWorkGas_BoundsThePrefixAndSignatures(TxFrame[] frames, TxFrameSignature[] signatures, ulong expected)
    {
        Transaction tx = CreateValidFrameTx(candidate =>
        {
            candidate.Frames = frames;
            candidate.FrameSignatures = signatures;
        });

        Assert.That(FrameTxValidation.ValidationWorkGas(tx), Is.EqualTo(expected));
    }

    [Test]
    public void TryGetExpiryDeadline_ReadsBigEndianDeadlineFromExpiryFrame()
    {
        const ulong expected = 0x0102_0304_0506_0708UL;
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        Transaction tx = CreateValidFrameTx(t => t.Frames =
        [
            SelfVerifyFrame(),
            new TxFrame(TxFrame.ModeVerify, flags: 0, Eip8141Constants.ExpiryVerifierAddress, gasLimit: 30_000, UInt256.Zero, data),
        ]);

        bool found = FrameTxValidation.TryGetExpiryDeadline(tx, out ulong deadline);

        Assert.That(found, Is.True);
        Assert.That(deadline, Is.EqualTo(expected));
    }

    [Test]
    public void TryGetExpiryDeadline_WithoutExpiryFrame_ReturnsFalse()
    {
        Transaction tx = CreateValidFrameTx(static _ => { });

        bool found = FrameTxValidation.TryGetExpiryDeadline(tx, out ulong deadline);

        Assert.That(found, Is.False);
        Assert.That(deadline, Is.EqualTo(0UL));
    }

    [Test]
    public void TryGetExpiryDeadline_NoFrames_ReturnsFalse()
    {
        Transaction tx = new() { Type = TxType.FrameTx, Frames = null };

        bool found = FrameTxValidation.TryGetExpiryDeadline(tx, out ulong deadline);

        Assert.That(found, Is.False);
        Assert.That(deadline, Is.EqualTo(0UL));
    }
}
