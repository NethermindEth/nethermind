// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>
/// EIP-8369 FOCIL Profile classification: Profile 1 (regular non-frame, non-blob), Profile 2
/// (recognized-shape frame txs within the VERIFY budget), and everything else Outside enforcement.
/// </summary>
public class Eip8369Tests
{
    [TestCase(TxType.Legacy)]
    [TestCase(TxType.AccessList)]
    [TestCase(TxType.EIP1559)]
    [TestCase(TxType.SetCode)]
    public void Classify_RegularNonBlobTx_IsProfileOne(TxType type)
    {
        Transaction tx = new() { Type = type, SenderAddress = TestItem.AddressA };
        Assert.That(Eip8369.Classify(tx), Is.EqualTo(FocilProfile.One));
    }

    [Test]
    public void Classify_BlobTx_IsOutside()
    {
        Transaction tx = new() { Type = TxType.Blob, SenderAddress = TestItem.AddressA, BlobVersionedHashes = [new byte[32]] };
        Assert.That(Eip8369.Classify(tx), Is.EqualTo(FocilProfile.Outside));
    }

    private static readonly TxFrame ExecutionFrame = new(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressB, 5_000_000, UInt256.Zero, default);

    // The four recognized Profile-2 shapes, plus the optional leading expiry-verifier frame.
    [Test]
    public void Classify_SelfVerify_IsProfileTwo() =>
        AssertProfile(FrameTx(SelfVerify(), ExecutionFrame), FocilProfile.Two);

    [Test]
    public void Classify_DeployThenSelfVerify_IsProfileTwo() =>
        AssertProfile(FrameTx(Deploy(), SelfVerify(), ExecutionFrame), FocilProfile.Two);

    [Test]
    public void Classify_OnlyVerifyThenPay_IsProfileTwo() =>
        AssertProfile(FrameTx(OnlyVerify(), Pay(), ExecutionFrame), FocilProfile.Two);

    [Test]
    public void Classify_DeployThenOnlyVerifyThenPay_IsProfileTwo() =>
        AssertProfile(FrameTx(Deploy(), OnlyVerify(), Pay(), ExecutionFrame), FocilProfile.Two);

    [Test]
    public void Classify_ExpiryThenSelfVerify_IsProfileTwo() =>
        AssertProfile(FrameTx(Expiry(), SelfVerify(), ExecutionFrame), FocilProfile.Two);

    [Test]
    public void Classify_BlobCarryingFrameTx_IsOutside()
    {
        Transaction tx = FrameTx(SelfVerify(), ExecutionFrame);
        tx.BlobVersionedHashes = [new byte[32]];
        Assert.That(Eip8369.Classify(tx), Is.EqualTo(FocilProfile.Outside));
    }

    [Test]
    public void Classify_WrongShapeFrameTx_IsOutside() =>
        AssertProfile(FrameTx(Deploy()), FocilProfile.Outside); // deploy with no verifying prefix frame

    [Test]
    public void Classify_VerifyFrameAfterPrefix_IsOutside() =>
        AssertProfile(FrameTx(SelfVerify(), SelfVerify()), FocilProfile.Outside);

    [Test]
    public void Classify_AtomicBatchFlagInPrefix_IsOutside() =>
        AssertProfile(FrameTx(SelfVerify(TxFrame.AtomicBatchFlag), ExecutionFrame), FocilProfile.Outside);

    [Test]
    public void Classify_OverBudgetFrameTx_IsOutside() =>
        AssertProfile(FrameTx(SelfVerify(gasLimit: Eip8369Constants.MaxVerifyGasPerTx + 1), ExecutionFrame), FocilProfile.Outside);

    [Test]
    public void Classify_AtBudgetFrameTx_IsProfileTwo() =>
        AssertProfile(FrameTx(SelfVerify(gasLimit: Eip8369Constants.MaxVerifyGasPerTx), ExecutionFrame), FocilProfile.Two);

    [Test]
    public void Profile2VerifyCost_MatchesValidationWorkGas()
    {
        Transaction tx = FrameTx(Expiry(), SelfVerify(), ExecutionFrame);
        Assert.That(Eip8369.Profile2VerifyCost(tx), Is.EqualTo(FrameTxValidation.ValidationWorkGas(tx)));
    }

    [Test]
    public void DefaultClaimedInclusionIndex_IsEndOfPayload()
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject, Build.A.Transaction.WithNonce(1).TestObject).TestObject;
        Assert.That(Eip8369.DefaultClaimedInclusionIndex(block), Is.EqualTo(block.Transactions.Length));
    }

    private static void AssertProfile(Transaction tx, FocilProfile expected) =>
        Assert.That(Eip8369.Classify(tx), Is.EqualTo(expected));

    private static Transaction FrameTx(params TxFrame[] frames) =>
        new() { Type = TxType.FrameTx, SenderAddress = TestItem.AddressA, Frames = frames, FrameSignatures = [] };

    private static TxFrame SelfVerify(byte extraFlags = 0, ulong gasLimit = 100_000) =>
        new(TxFrame.ModeVerify, (byte)(TxFrame.ApproveExecutionAndPayment | extraFlags), target: null, gasLimit, UInt256.Zero, default);

    private static TxFrame OnlyVerify() => new(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, 40_000, UInt256.Zero, default);

    private static TxFrame Pay() => new(TxFrame.ModeVerify, TxFrame.ApprovePayment, TestItem.AddressB, 30_000, UInt256.Zero, default);

    private static TxFrame Deploy() => new(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, target: null, 50_000, UInt256.Zero, default);

    private static TxFrame Expiry() =>
        new(TxFrame.ModeVerify, flags: 0, Eip8141Constants.ExpiryVerifierAddress, 30_000, UInt256.Zero, new byte[Eip8141Constants.ExpiryDataLength]);
}
