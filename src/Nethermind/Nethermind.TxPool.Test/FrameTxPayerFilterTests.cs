// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class FrameTxPayerFilterTests
{
    [Test]
    public void Accept_FrameTx_RecordsResolvedPayerAndAccepts()
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(TestItem.AddressA, 1 * Unit.Ether);
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
            FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressA, default, new byte[TxFrameSignature.Secp256k1SignatureLength])],
        };

        AcceptTxResult result = Accept(state, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.PayerAddress, Is.EqualTo(TestItem.AddressA));
        }
    }

    [Test]
    public void Accept_NonFrameTx_LeavesPayerUnsetAndAccepts()
    {
        Transaction tx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;

        AcceptTxResult result = Accept(new TestReadOnlyStateProvider(), tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.PayerAddress, Is.Null);
        }
    }

    [Test]
    public void Accept_FrameTxWithNoPayer_Rejects()
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(TestItem.AddressA, 1 * Unit.Ether);
        // only_verify with no following pay frame never approves a payer: refused, but not as Invalid,
        // so the peer that relayed it is not disconnected.
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 100_000, UInt256.Zero, default)],
            FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressA, default, new byte[TxFrameSignature.Secp256k1SignatureLength])],
        };

        AcceptTxResult result = Accept(state, tx);

        Assert.That(result, Is.EqualTo(AcceptTxResult.FrameTxNoPayer));
    }

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, Transaction tx)
    {
        FrameTxPayerFilter filter = new(LimboLogs.Instance.GetClassLogger<FrameTxPayerFilterTests>());
        TxFilteringState filteringState = new(tx, state);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }
}
