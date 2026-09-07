// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Ethereum.Transaction.Test;

/// <summary>
/// Guards that the <c>transaction_tests</c> harness applies EIP-8141 <c>validate_signature</c>.
/// </summary>
/// <remarks>
/// The rule is stateless but sits outside <c>TxValidator</c>, so a harness stopping there accepts every
/// frame transaction whose only defect is a signature — silently passing the reject-shaped fixtures. Each
/// case perturbs only the signature entry of one otherwise valid transaction, so the control below fails
/// with them if the shared payload stops validating for an unrelated reason.
/// </remarks>
public class FrameTxSignatureTransactionTestTests : TransactionTestBase
{
    private const string Fork = "Eip8141Prototype";
    private static readonly Ecdsa s_ecdsa = new();

    [Test]
    public void ValidFrameSignatureIsAccepted()
    {
        Result result = RunTest(TestFor(SignedFrameTx(), expectedException: null));

        Assert.That((bool)result, Is.True, result.Error);
    }

    [Test]
    public void HighSFrameSignatureIsRejectedAsInvalidSignature()
    {
        // EIP-2 low-s: the malleable complement of an otherwise verifying signature.
        Nethermind.Core.Transaction tx = SignedFrameTx();
        byte[] raw = tx.FrameSignatures![0].Signature.ToArray();
        UInt256 s = new(raw.AsSpan(33, 32), isBigEndian: true);
        (SecP256k1Curve.N - s).ToBigEndian(raw.AsSpan(33, 32));
        tx.FrameSignatures = [WithSignature(tx.FrameSignatures[0], raw)];

        Result result = RunTest(TestFor(tx, "TransactionException.TYPE_6_INVALID_SIGNATURE"));

        Assert.That((bool)result, Is.True, result.Error);
    }

    [Test]
    public void FrameSignatureFromAnotherKeyIsRejectedAsInvalidFrameFormat()
    {
        // The fixtures file a signer that does not match the recovered address as a format failure.
        Nethermind.Core.Transaction tx = SignedFrameTx(signer: TestItem.PrivateKeyC.Address);

        Result result = RunTest(TestFor(tx, "TransactionException.TYPE_6_INVALID_FRAME_FORMAT"));

        Assert.That((bool)result, Is.True, result.Error);
    }

    /// <summary>A frame transaction that <c>TxValidator</c> accepts, signed over its canonical sig hash.</summary>
    private static Nethermind.Core.Transaction SignedFrameTx(Address signer = null)
    {
        Nethermind.Core.Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = MainnetSpecProvider.Instance.ChainId,
            Nonce = 0,
            SenderAddress = TestItem.PrivateKeyB.Address,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, null, 100_000, default, default)],
            GasPrice = 1,
            DecodedMaxFeePerGas = 100,
        };

        // compute_sig_hash covers the entry's metadata and elides only its raw bytes, so the placeholder
        // entry must be installed before hashing.
        TxFrameSignature entry = new(TxFrameSignature.SchemeSecp256k1, signer, default, default);
        tx.FrameSignatures = [entry];
        Signature signature = s_ecdsa.Sign(TestItem.PrivateKeyB, FrameTxSigHash.ComputeValue(tx));

        byte[] raw = new byte[TxFrameSignature.Secp256k1SignatureLength];
        raw[0] = signature.RecoveryId; // strict yParity encoding (0/1)
        signature.Bytes.CopyTo(raw.AsSpan(1));
        tx.FrameSignatures = [WithSignature(entry, raw)];
        return tx;
    }

    private static TxFrameSignature WithSignature(TxFrameSignature entry, byte[] raw) =>
        new(entry.Scheme, entry.Signer, entry.Msg, raw);

    private static TransactionTest TestFor(Nethermind.Core.Transaction tx, string expectedException) =>
        new()
        {
            Name = TestContext.CurrentContext.Test.Name,
            Fork = Fork,
            TxBytes = Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true),
            ExpectedException = expectedException,
        };
}
