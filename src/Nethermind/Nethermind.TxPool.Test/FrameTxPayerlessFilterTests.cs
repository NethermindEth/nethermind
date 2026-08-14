// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class FrameTxPayerlessFilterTests
{
    // The filter takes no ECDSA/signature validator, so a structural rejection cannot invoke signature
    // verification even when the (invalid) signature list is large and would be expensive to recover.
    [Test]
    public void Accept_PayerlessPrefixWithLargeSignatureList_RejectedWithoutSignatureVerification()
    {
        long before = Metrics.PendingTransactionsFrameTxNoPayer;
        // A lone only_verify frame never approves a payer regardless of the signatures.
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 100_000, UInt256.Zero, default)],
            FrameSignatures = Enumerable.Range(0, 1024)
                .Select(_ => new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressA, default, new byte[TxFrameSignature.Secp256k1SignatureLength]))
                .ToArray(),
        };

        AcceptTxResult result = Accept(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.FrameTxNoPayer));
            Assert.That(Metrics.PendingTransactionsFrameTxNoPayer, Is.EqualTo(before + 1));
        }
    }

    [Test]
    public void Accept_ExpiryOnlyPrefix_Rejected()
    {
        byte[] data = new byte[Eip8141Constants.ExpiryDataLength];
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(TxFrame.ModeVerify, flags: 0, Eip8141Constants.ExpiryVerifierAddress, gasLimit: 30_000, UInt256.Zero, data)],
            FrameSignatures = [],
        };

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.FrameTxNoPayer));
    }

    [Test]
    public void Accept_PayerApprovingPrefix_Accepted()
    {
        // self_verify approves a payer, so the structural filter must let it through to later filters.
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
            FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressA, default, new byte[TxFrameSignature.Secp256k1SignatureLength])],
        };

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_NonFrameTx_Accepted()
    {
        Transaction tx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
    }

    private static AcceptTxResult Accept(Transaction tx)
    {
        FrameTxPayerlessFilter filter = new(LimboLogs.Instance.GetClassLogger<FrameTxPayerlessFilterTests>());
        TestReadOnlyStateProvider state = new();
        TxFilteringState filteringState = new(tx, state);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }
}
