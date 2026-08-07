// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using Nethermind.Core;
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

    // Per-scheme signature-verification costs counted against MAX_VERIFY_GAS.
    private const ulong SecpCost = Eip8141Constants.Secp256k1VerificationGasCost;   // 2_800
    private const ulong P256Cost = Eip8141Constants.P256VerificationGasCost;        // 6_700
    private const ulong ArbitraryCost = Eip8141Constants.ArbitraryVerificationGasCost; // 100
    private const ulong Max = Eip8141Constants.MaxVerifyGas;                        // 100_000

    private static IEnumerable<TestCaseData> VerifyGasCases()
    {
        // self_verify: the single verify frame is the whole prefix.
        yield return Case("self_verify within budget", [SelfVerify(90_000)], [Secp(Sender)], AcceptTxResult.Accepted);
        yield return Case("self_verify at budget", [SelfVerify(Max - SecpCost)], [Secp(Sender)], AcceptTxResult.Accepted);
        yield return Case("self_verify one over budget", [SelfVerify(Max - SecpCost + 1)], [Secp(Sender)], AcceptTxResult.VerifyGasExceeded);

        // only_verify + pay: both frames are in the prefix; both signatures are counted.
        yield return Case("signature cost alone pushes over budget",
            [OnlyVerify(Max - SecpCost), Pay(Sponsor, 0)], [Secp(Sender), Secp(Sponsor)], AcceptTxResult.VerifyGasExceeded);
        yield return Case("only_verify+pay summed prefix gas over budget",
            [OnlyVerify(60_000), Pay(Sponsor, 60_000)], [Secp(Sender), Secp(Sponsor)], AcceptTxResult.VerifyGasExceeded);

        // A leading expiry_verify frame is skipped for shape matching but its gas counts.
        yield return Case("expiry frame gas counts toward prefix",
            [Expiry(9999, 50_000), SelfVerify(48_000)], [Secp(Sender)], AcceptTxResult.VerifyGasExceeded);

        // Frames after the prefix are not counted.
        yield return Case("frames after prefix not counted",
            [SelfVerify(10_000), Frame(TxFrame.ModeSender, ulong.MaxValue / 2)], [Secp(Sender)], AcceptTxResult.Accepted);

        // Unrecognized prefixes (e.g. a leading deploy frame) and expiry-only prefixes are not bounded here.
        yield return Case("unrecognized (deploy-led) prefix passes through",
            [Frame(TxFrame.ModeDefault, ulong.MaxValue / 2), SelfVerify(10_000)], [Secp(Sender)], AcceptTxResult.Accepted);
        yield return Case("expiry-only prefix has no verify frame, passes through",
            [Expiry(9999, ulong.MaxValue / 2)], [], AcceptTxResult.Accepted);

        // Overflow in the summed prefix gas is treated as definitively over budget.
        yield return Case("prefix gas overflow rejected",
            [OnlyVerify(ulong.MaxValue), Pay(Sponsor, 10)], [Secp(Sender)], AcceptTxResult.VerifyGasExceeded);

        // P256 signature cost (6_700) exercises the P256 branch of the scheme→cost switch.
        yield return Case("P256 signature at budget", [SelfVerify(Max - P256Cost)], [P256(Sender)], AcceptTxResult.Accepted);
        yield return Case("P256 signature one over budget", [SelfVerify(Max - P256Cost + 1)], [P256(Sender)], AcceptTxResult.VerifyGasExceeded);

        // Arbitrary signature cost (100) exercises the Arbitrary branch.
        yield return Case("arbitrary signature at budget", [SelfVerify(Max - ArbitraryCost)], [Arbitrary()], AcceptTxResult.Accepted);
        yield return Case("arbitrary signature one over budget", [SelfVerify(Max - ArbitraryCost + 1)], [Arbitrary()], AcceptTxResult.VerifyGasExceeded);

        // Mixed-scheme signatures (secp256k1 2_800 + P256 6_700 = 9_500) are summed per scheme.
        yield return Case("mixed-scheme signatures at budget",
            [OnlyVerify(Max - SecpCost - P256Cost - 500), Pay(Sponsor, 500)], [Secp(Sender), P256(Sponsor)], AcceptTxResult.Accepted);
        yield return Case("mixed-scheme signatures one over budget",
            [OnlyVerify(Max - SecpCost - P256Cost - 500), Pay(Sponsor, 501)], [Secp(Sender), P256(Sponsor)], AcceptTxResult.VerifyGasExceeded);
    }

    [TestCaseSource(nameof(VerifyGasCases))]
    public AcceptTxResult BoundsVerifyGas(TxFrame[] frames, TxFrameSignature[] signatures) =>
        Accept(FrameTx(frames, signatures));

    [Test]
    public void NonFrameTx_PassesThrough()
    {
        Transaction tx = Build.A.Transaction.WithSenderAddress(Sender).WithGasLimit(long.MaxValue).TestObject;

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void LocalTx_ExemptFromBound()
    {
        // The bound targets gossiped public-mempool traffic; a locally-submitted over-budget tx is exempt.
        Transaction tx = FrameTx([SelfVerify(Max)], [Secp(Sender)]);

        Assert.That(Accept(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [TestCase(TxFrameSignature.SchemeArbitrary, ArbitraryCost)]
    [TestCase(TxFrameSignature.SchemeSecp256k1, SecpCost)]
    [TestCase(TxFrameSignature.SchemeP256, P256Cost)]
    public void SignatureVerificationGasCost_MapsSchemeToConstant(byte scheme, ulong expected) =>
        Assert.That(Eip8141Constants.SignatureVerificationGasCost(scheme), Is.EqualTo(expected));

    private static TestCaseData Case(string name, TxFrame[] frames, TxFrameSignature[] signatures, AcceptTxResult expected) =>
        new TestCaseData(frames, signatures).Returns(expected).SetName(name);

    private static AcceptTxResult Accept(Transaction tx, TxHandlingOptions handlingOptions = TxHandlingOptions.None)
    {
        FrameTxVerifyGasFilter filter = new(LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTests>());
        TxFilteringState filteringState = new(tx, Substitute.For<IAccountStateProvider>());
        return filter.Accept(tx, ref filteringState, handlingOptions);
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

    private static TxFrameSignature P256(Address signer) =>
        new(TxFrameSignature.SchemeP256, signer, default, default);

    private static TxFrameSignature Arbitrary() =>
        new(TxFrameSignature.SchemeArbitrary, signer: null, default, default);

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
