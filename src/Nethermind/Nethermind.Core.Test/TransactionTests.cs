// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Core.Test;

public class TransactionTests
{
    [Test]
    public void When_to_not_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = Address.Zero;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.IsMessageCall, Is.True, nameof(Transaction.IsMessageCall));
            Assert.That(transaction.IsContractCreation, Is.False, nameof(Transaction.IsContractCreation));
        }
    }

    [Test]
    public void When_to_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.IsMessageCall, Is.False, nameof(Transaction.IsMessageCall));
            Assert.That(transaction.IsContractCreation, Is.True, nameof(Transaction.IsContractCreation));
        }
    }

    [TestCase(1, true)]
    [TestCase(300, true)]
    public void Supports1559_returns_expected_results(int decodedFeeCap, bool expectedSupports1559)
    {
        Transaction transaction = new();
        transaction.DecodedMaxFeePerGas = (uint)decodedFeeCap;
        transaction.Type = TxType.EIP1559;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.DecodedMaxFeePerGas, Is.EqualTo(transaction.MaxFeePerGas));
            Assert.That(transaction.Supports1559, Is.EqualTo(expectedSupports1559));
        }
    }

    [Test]
    public void FrameTx_type_value_is_0x06() => Assert.That((byte)TxType.FrameTx, Is.EqualTo(0x06));

    [Test]
    public void FrameFields_OnNewTransaction_AreNull()
    {
        Transaction transaction = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Frames, Is.Null, nameof(Transaction.Frames));
            Assert.That(transaction.FrameSignatures, Is.Null, nameof(Transaction.FrameSignatures));
        }
    }

    [TestCase(TxType.Legacy, false, TestName = "Legacy")]
    [TestCase(TxType.AccessList, false, TestName = "AccessList")]
    [TestCase(TxType.EIP1559, false, TestName = "EIP1559")]
    [TestCase(TxType.Blob, false, TestName = "Blob")]
    [TestCase(TxType.SetCode, false, TestName = "SetCode")]
    [TestCase(TxType.FrameTx, true, TestName = "FrameTx")]
    [TestCase(TxType.DepositTx, false, TestName = "DepositTx")]
    public void SupportsFrames_PerTxType_MatchesExpectation(TxType txType, bool expected)
    {
        Transaction transaction = new() { Type = txType };
        Assert.That(transaction.SupportsFrames, Is.EqualTo(expected));
    }

    [Test]
    public void FrameTx_TypePredicates_MatchEip8141Payload()
    {
        Transaction transaction = new() { Type = TxType.FrameTx };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Supports1559, Is.True, "frame txs carry EIP-1559 fee fields");
            Assert.That(transaction.SupportsAccessList, Is.False, "frame txs have no access list field");
            Assert.That(transaction.SupportsAuthorizationList, Is.False, "frame txs have no authorization list");
            Assert.That(transaction.SupportsBlobs, Is.False, "blob sidecars are not supported by the prototype");
        }
    }

    [Test]
    public void CopyTo_WithFrameFields_CopiesFrames()
    {
        Transaction source = new()
        {
            Type = TxType.FrameTx,
            Frames = [new Eip8141.Frame(Eip8141.Frame.ModeVerify, Eip8141.Frame.ApproveExecutionAndPayment, null, 50_000, 0, [])],
            FrameSignatures = [new Eip8141.FrameSignature(Eip8141.FrameSignature.SchemeSecp256k1, null, [], new byte[65])],
        };
        Transaction destination = new();

        source.CopyTo(destination);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(destination.Frames, Is.SameAs(source.Frames), nameof(Transaction.Frames));
            Assert.That(destination.FrameSignatures, Is.SameAs(source.FrameSignatures), nameof(Transaction.FrameSignatures));
        }
    }
}
