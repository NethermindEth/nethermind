// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

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
        TxFrameSignature[] signatures = new TxFrameSignature[1024];
        for (int i = 0; i < signatures.Length; i++)
        {
            signatures[i] = Secp256k1Signature(TestItem.AddressA);
        }

        Transaction tx = FrameTx(TestItem.AddressA, signatures, OnlyVerify(PrefixFrameGas));

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
        Transaction tx = FrameTx(Expiry());

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.FrameTxNoPayer));
    }

    [Test]
    public void Accept_PayerApprovingPrefix_Accepted()
    {
        // self_verify approves a payer, so the structural filter must let it through to later filters.
        Transaction tx = FrameTx(TestItem.AddressA, [Secp256k1Signature(TestItem.AddressA)], SelfVerify(PrefixFrameGas));

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
