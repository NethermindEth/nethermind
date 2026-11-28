// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Ethereum.Transaction.Test;

/// <summary>
/// Guards that the <c>transaction_tests</c> harness verifies EIP-8141 claims carried in the pooled wrapper.
/// </summary>
/// <remarks>
/// The rule is stateless but sits outside <c>TxValidator</c>, so a harness stopping there accepts every
/// frame transaction whose only defect is a witness — silently passing the reject-shaped fixtures. Each
/// case perturbs only the witness of one otherwise valid transaction, so the control below fails
/// with them if the shared payload stops validating for an unrelated reason.
/// </remarks>
public class FrameTxSignatureTransactionTestTests : TransactionTestBase
{
    private const string Fork = "Eip8141Prototype";
    private static readonly Ecdsa _ecdsa = new();

    [Test]
    public void ValidFrameClaimIsAccepted()
    {
        Result result = RunTest(TestFor(SignedFrameTx(), expectedException: null));

        Assert.That((bool)result, Is.True, result.Error);
    }

    [Test]
    public void HighSWitnessIsRejectedAsInvalidSignature()
    {
        // EIP-2 low-s: the malleable complement of an otherwise verifying signature.
        Nethermind.Core.Transaction tx = SignedFrameTx();
        byte[] raw = tx.ClaimWitnesses![0];
        UInt256 s = new(raw.AsSpan(33, 32), isBigEndian: true);
        (SecP256k1Curve.N - s).ToBigEndian(raw.AsSpan(33, 32));

        Result result = RunTest(TestFor(tx, "TransactionException.TYPE_6_INVALID_SIGNATURE"));

        Assert.That((bool)result, Is.True, result.Error);
    }

    [Test]
    public void WitnessFromAnotherKeyIsRejectedAsInvalidSignature()
    {
        Nethermind.Core.Transaction tx = SignedFrameTx(signer: TestItem.PrivateKeyC.Address);

        Result result = RunTest(TestFor(tx, "TransactionException.TYPE_6_INVALID_SIGNATURE"));

        Assert.That((bool)result, Is.True, result.Error);
    }

    /// <summary>A frame transaction that <c>TxValidator</c> accepts, with a SECP256K1 claim over its canonical sig hash.</summary>
    private static Nethermind.Core.Transaction SignedFrameTx(Address signer = null)
    {
        Nethermind.Core.Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = MainnetSpecProvider.Instance.ChainId,
            Nonce = 0,
            SenderAddress = TestItem.PrivateKeyB.Address,
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, 100_000, default, default)],
            FrameClaims = [FrameTxTestFrames.Secp256k1Claim(signer)],
            GasPrice = 1,
            DecodedMaxFeePerGas = 100,
        };

        tx.ClaimWitnesses = [FrameTxTestFrames.Secp256k1SignatureBytes(_ecdsa.Sign(TestItem.PrivateKeyB, FrameTxSigHash.ComputeValue(tx)))];
        return tx;
    }

    private static TransactionTest TestFor(Nethermind.Core.Transaction tx, string expectedException) =>
        new()
        {
            Name = TestContext.CurrentContext.Test.Name,
            Fork = Fork,
            TxBytes = Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm).ToHexString(true),
            ExpectedException = expectedException,
        };
}
