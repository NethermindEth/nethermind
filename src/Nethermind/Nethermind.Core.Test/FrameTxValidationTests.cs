// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
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

        // EIP-8288 disabled: the base EIP-8141 constraint matrix (mode 3 remains an invalid mode).
        bool wellFormed = FrameTxValidation.IsWellFormed(tx, ReleaseSpecSubstitute.Create(), out string? error);

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

    [TestCaseSource(nameof(DependencyConstraintCases))]
    public void IsWellFormed_Eip8288DependencyFrames_ReturnsExpectedError(Action<Transaction> mutate, string? expectedError)
    {
        Transaction tx = CreateValidFrameTx(mutate);
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        spec.IsEip8288Enabled.Returns(true);

        bool wellFormed = FrameTxValidation.IsWellFormed(tx, spec, out string? error);

        Assert.That(wellFormed, Is.EqualTo(expectedError is null));
        Assert.That(error, Is.EqualTo(expectedError));
    }

    private static IEnumerable<TestCaseData> DependencyConstraintCases()
    {
        yield return DepCase("SingleLeanSphincsDependency_Valid",
            static tx => tx.Frames = [DepFrame(Eip8288Constants.LeanSphincsScheme)], null);
        yield return DepCase("SingleLeanStarkDependency_Valid",
            static tx => tx.Frames = [DepFrame(Eip8288Constants.LeanStarkScheme)], null);
        yield return DepCase("MixedSchemesInOneFrame_Valid",
            static tx => tx.Frames = [DepFrame(Eip8288Constants.LeanSphincsScheme, Eip8288Constants.LeanStarkScheme)], null);

        // data must be a non-empty multiple of 96 bytes.
        yield return DepCase("EmptyData_DependencyFrameDataLength",
            static tx => tx.Frames = [new TxFrame(TxFrame.ModeDepVerify, 0, null, 0, UInt256.Zero, Array.Empty<byte>())],
            FrameTxValidation.DependencyFrameDataLength);
        yield return DepCase("UnalignedData_DependencyFrameDataLength",
            static tx => tx.Frames = [new TxFrame(TxFrame.ModeDepVerify, 0, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, new byte[95])],
            FrameTxValidation.DependencyFrameDataLength);

        // at most MAX_DEPENDENCIES_PER_FRAME triples.
        yield return DepCase("TooManyDependenciesPerFrame_TooManyDependenciesPerFrame",
            static tx => tx.Frames = [DepFrame(Enumerable.Repeat(Eip8288Constants.LeanSphincsScheme, Eip8288Constants.MaxDependenciesPerFrame + 1).ToArray())],
            FrameTxValidation.TooManyDependenciesPerFrame);

        // first 31 bytes of each triple must be zero.
        yield return DepCase("NonZeroPadding_DependencyPaddingNotZero",
            static tx => tx.Frames = [DepFrameRaw([Eip8288Constants.LeanSphincsScheme], mutateData: static data => data[0] = 1)],
            FrameTxValidation.DependencyPaddingNotZero);

        // scheme must be LEANSPHINCS or LEANSTARK.
        yield return DepCase("UnknownScheme_InvalidDependencyScheme",
            static tx => tx.Frames = [DepFrame(0x12)], FrameTxValidation.InvalidDependencyScheme);

        // gas_limit must equal the sum of per-scheme verification gas.
        yield return DepCase("GasLimitMismatch_DependencyFrameGasMismatch",
            static tx => tx.Frames = [DepFrameRaw([Eip8288Constants.LeanSphincsScheme], gasLimit: 1)],
            FrameTxValidation.DependencyFrameGasMismatch);

        // target None, value 0, flags 0.
        yield return DepCase("NonNullTarget_DependencyFrameShape",
            static tx => tx.Frames = [DepFrameRaw([Eip8288Constants.LeanSphincsScheme], target: TestItem.AddressB)],
            FrameTxValidation.DependencyFrameShape);
        yield return DepCase("NonZeroValue_DependencyFrameShape",
            static tx => tx.Frames = [DepFrameRaw([Eip8288Constants.LeanSphincsScheme], value: UInt256.One)],
            FrameTxValidation.DependencyFrameShape);
        yield return DepCase("NonZeroFlags_DependencyFrameShape",
            static tx => tx.Frames = [DepFrameRaw([Eip8288Constants.LeanSphincsScheme], flags: TxFrame.ApprovePayment)],
            FrameTxValidation.DependencyFrameShape);

        // per-transaction limits MAX_SIGS_PER_TX and MAX_STARKS_PER_TX.
        yield return DepCase("TooManySigDeps_TooManySigDeps",
            static tx => tx.Frames = [DepFrame(Enumerable.Repeat(Eip8288Constants.LeanSphincsScheme, Eip8288Constants.MaxSigsPerTx + 1).ToArray())],
            FrameTxValidation.TooManySigDeps);
        yield return DepCase("TooManyStarkDeps_TooManyStarkDeps",
            static tx => tx.Frames = [DepFrame(Eip8288Constants.LeanStarkScheme, Eip8288Constants.LeanStarkScheme)],
            FrameTxValidation.TooManyStarkDeps);
    }

    private static TestCaseData Case(string name, Action<Transaction> mutate, string? expectedError) =>
        new TestCaseData(mutate, expectedError).SetName($"IsWellFormed_{name}");

    private static TestCaseData DepCase(string name, Action<Transaction> mutate, string? expectedError) =>
        new TestCaseData(mutate, expectedError).SetName($"IsWellFormed_Eip8288_{name}");

    private static TxFrame DepFrame(params byte[] schemes) => DepFrameRaw(schemes);

    private static TxFrame DepFrameRaw(byte[] schemes, ulong? gasLimit = null, Address? target = null, UInt256 value = default, byte flags = 0, Action<byte[]>? mutateData = null)
    {
        ulong gas = 0;
        byte[] data = new byte[schemes.Length * Eip8288Constants.DependencyTripleLength];
        for (int i = 0; i < schemes.Length; i++)
        {
            int baseOffset = i * Eip8288Constants.DependencyTripleLength;
            data[baseOffset + 31] = schemes[i];
            data[baseOffset + 32] = 0xAA; // arbitrary non-zero data_hash / vk content
            data[baseOffset + 64] = 0xBB;
            gas += schemes[i] == Eip8288Constants.LeanStarkScheme
                ? Eip8288Constants.LeanStarkVerificationGas
                : Eip8288Constants.LeanSphincsVerificationGas;
        }

        mutateData?.Invoke(data);
        return new TxFrame(TxFrame.ModeDepVerify, flags, target, gasLimit ?? gas, value, data);
    }

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
}
