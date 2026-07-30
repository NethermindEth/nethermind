// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>
/// EIP-8141 <c>MAX_VERIFY_GAS</c> admission bound: a frame tx is rejected once its validation-prefix
/// gas plus signature-validation cost exceeds the budget, and accepted while it stays within.
/// </summary>
public class FrameTxVerifyGasFilterTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Sponsor = TestItem.AddressB;

    // secp256k1 verification = 2_800, so the prefix gas budget below it is 97_200.
    private const ulong SecpCost = Eip8141Constants.Secp256k1VerificationGasCost;

    [Test]
    public void Accept_SelfVerify_WithinBudget_Accepted()
    {
        // 90_000 prefix gas + 2_800 signature = 92_800 <= 100_000.
        Transaction tx = FrameTx([SelfVerify(90_000)], [Secp(Sender)]);

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_SelfVerify_AtBudget_Accepted()
    {
        // Exactly MAX_VERIFY_GAS: (100_000 - 2_800) prefix gas + 2_800 signature = 100_000.
        Transaction tx = FrameTx([SelfVerify(Eip8141Constants.MaxVerifyGas - SecpCost)], [Secp(Sender)]);

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_SelfVerify_OneOverBudget_Rejected()
    {
        // One gas over: prefix (100_000 - 2_800 + 1) + 2_800 signature = 100_001.
        Transaction tx = FrameTx([SelfVerify(Eip8141Constants.MaxVerifyGas - SecpCost + 1)], [Secp(Sender)]);

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.VerifyGasExceeded));
    }

    [Test]
    public void Accept_SignatureCostAlonePushesOverBudget_Rejected()
    {
        // Prefix gas is within budget, but two secp256k1 signatures (5_600) tip the total over.
        Transaction tx = FrameTx([OnlyVerify(Eip8141Constants.MaxVerifyGas - SecpCost), Pay(Sponsor, 0)], [Secp(Sender), Secp(Sponsor)]);

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.VerifyGasExceeded));
    }

    [Test]
    public void Accept_OnlyVerifyPay_SummedPrefixGasOverBudget_Rejected()
    {
        // The pay frame's gas counts toward the prefix: 60_000 + 60_000 + 2_800 > 100_000.
        Transaction tx = FrameTx([OnlyVerify(60_000), Pay(Sponsor, 60_000)], [Secp(Sender), Secp(Sponsor)]);

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.VerifyGasExceeded));
    }

    [Test]
    public void Accept_ExpiryFrameGasCountsTowardPrefix()
    {
        // expiry (50_000) + self_verify (48_000) + 2_800 = 100_800 > 100_000.
        Transaction tx = FrameTx([Expiry(9999, 50_000), SelfVerify(48_000)], [Secp(Sender)]);

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.VerifyGasExceeded));
    }

    [Test]
    public void Accept_FramesAfterPrefix_NotCounted()
    {
        // A huge user_op after the self_verify prefix does not count toward the verify-gas bound.
        Transaction tx = FrameTx([SelfVerify(10_000), Frame(TxFrame.ModeSender, ulong.MaxValue / 2)], [Secp(Sender)]);

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_UnrecognizedPrefix_PassesThrough()
    {
        // A leading DEFAULT (deploy) frame is not analyzed here; the bound is not enforced (deferred).
        Transaction tx = FrameTx([Frame(TxFrame.ModeDefault, ulong.MaxValue / 2), SelfVerify(10_000)], [Secp(Sender)]);

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_PrefixGasOverflow_Rejected()
    {
        Transaction tx = FrameTx([OnlyVerify(ulong.MaxValue), Pay(Sponsor, 10)], [Secp(Sender)]);

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.VerifyGasExceeded));
    }

    [Test]
    public void Accept_NonFrameTx_PassesThrough()
    {
        Transaction tx = Build.A.Transaction.WithSenderAddress(Sender).WithGasLimit(long.MaxValue).TestObject;

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    private static AcceptTxResult Accept(Transaction tx)
    {
        FrameTxVerifyGasFilter filter = new(LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTests>());
        TxFilteringState filteringState = new(tx, Substitute.For<IAccountStateProvider>());
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    private static Transaction FrameTx(TxFrame[] frames, TxFrameSignature[] signatures) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = Sender,
        Frames = frames,
        FrameSignatures = signatures,
    };

    private static TxFrameSignature Secp(Address signer) =>
        new(TxFrameSignature.SchemeSecp256k1, signer, default, new byte[TxFrameSignature.Secp256k1SignatureLength]);

    private static TxFrame SelfVerify(ulong gasLimit) =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit, UInt256.Zero, default);

    private static TxFrame OnlyVerify(ulong gasLimit) =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit, UInt256.Zero, default);

    private static TxFrame Pay(Address target, ulong gasLimit) =>
        new(TxFrame.ModeVerify, TxFrame.ApprovePayment, target, gasLimit, UInt256.Zero, default);

    private static TxFrame Frame(byte mode, ulong gasLimit) =>
        new(mode, flags: 0, target: null, gasLimit, UInt256.Zero, default);

    private static TxFrame Expiry(ulong deadline, ulong gasLimit)
    {
        byte[] data = new byte[Eip8141Constants.ExpiryDataLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data, deadline);
        return new TxFrame(TxFrame.ModeVerify, flags: 0, Eip8141Constants.ExpiryVerifierAddress, gasLimit, UInt256.Zero, data);
    }
}
