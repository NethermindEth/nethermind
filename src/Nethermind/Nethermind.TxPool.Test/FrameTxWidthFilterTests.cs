// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>MATCHA sender-keyed width: keyed-nonce frame transactions beyond the sender's free baseline spend width earned from included gas.</summary>
public class FrameTxWidthFilterTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly UInt256 NonceKey = 0xbeef;
    private const ulong Cost = 21_000;
    private const int Baseline = 2;

    [TestCase(0, true, TestName = "no pending transactions")]
    [TestCase(Baseline - 1, true, TestName = "last free admission")]
    [TestCase(Baseline, false, TestName = "first admission beyond the baseline")]
    [TestCase(Baseline + 1, false, TestName = "further beyond the baseline")]
    public void Accept_FirstBaselineAdmissionsAreFreeWithoutWidth(int pending, bool accepted)
    {
        SenderWidthCache cache = new();

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: (ulong)pending), PendingKeyedTxs(pending));

        Assert.That(result, Is.EqualTo(accepted ? AcceptTxResult.Accepted : AcceptTxResult.WidthUnmet));
    }

    [TestCase(0ul, false, TestName = "zero width")]
    [TestCase(Cost - 1, false, TestName = "width one short of the cost")]
    [TestCase(Cost, true, TestName = "width exactly covering the cost")]
    [TestCase(Cost + 1, true, TestName = "width above the cost")]
    public void Accept_BeyondBaseline_SpendsEarnedWidth(ulong earned, bool accepted)
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, earned);

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: Baseline), PendingKeyedTxs(Baseline));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(accepted ? AcceptTxResult.Accepted : AcceptTxResult.WidthUnmet));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)(accepted ? earned - Cost : earned)), "a rejection spends nothing");
        }
    }

    [Test]
    public void Accept_ChargedAdmission_IsRefundedWhenTheTransactionLeavesThePool()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, Cost);
        Transaction tx = KeyedTx(nonceSeq: Baseline);

        AcceptTxResult admitted = Accept(cache, tx, PendingKeyedTxs(Baseline));
        cache.RefundCharge(tx.Hash!.ValueHash256);
        AcceptTxResult readmitted = Accept(cache, tx, PendingKeyedTxs(Baseline));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(admitted, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(readmitted, Is.EqualTo(AcceptTxResult.Accepted), "the refund restores what admission took");
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero));
        }
    }

    // A bump displaces a pending entry rather than adding one, so it stays inside what the incumbent's
    // admission already paid for; a new sequence joins the pending set and must pay.
    [TestCase(true, true, TestName = "fee bump of a pending keyed transaction")]
    [TestCase(false, false, TestName = "next sequence from the same sender")]
    public void Accept_ReplacementIsNotAnAdditionalAdmission(bool replaces, bool accepted)
    {
        SenderWidthCache cache = new();
        Transaction incoming = KeyedTx(nonceSeq: (ulong)(replaces ? Baseline - 1 : Baseline), gasPrice: 2);

        AcceptTxResult result = Accept(cache, incoming, PendingKeyedTxs(Baseline));

        Assert.That(result, Is.EqualTo(accepted ? AcceptTxResult.Accepted : AcceptTxResult.WidthUnmet));
    }

    [TestCase(true, TestName = "width enabled")]
    [TestCase(false, TestName = "width disabled")]
    public void Accept_DisabledWidth_NeverRejectsOrTouchesTheLedger(bool enabled)
    {
        SenderWidthCache cache = new();
        long rejectionsBefore = Metrics.PendingTransactionsFrameTxWidthUnmet;

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: Baseline), PendingKeyedTxs(Baseline), enabled);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(enabled ? AcceptTxResult.WidthUnmet : AcceptTxResult.Accepted));
            Assert.That(Metrics.PendingTransactionsFrameTxWidthUnmet - rejectionsBefore, Is.EqualTo(enabled ? 1 : 0));
        }
    }

    [TestCaseSource(nameof(ChargeableCases))]
    public void Accept_ChargesOnlyKeyedNonceFrameTransactions(Func<Transaction> build, bool charged)
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, Cost);

        AcceptTxResult result = Accept(cache, build(), PendingKeyedTxs(Baseline));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)(charged ? 0 : Cost)));
        }
    }

    private static IEnumerable<TestCaseData> ChargeableCases()
    {
        yield return new TestCaseData(() => KeyedTx(nonceSeq: Baseline), true).SetName("keyed-nonce frame transaction is charged");
        yield return new TestCaseData(() => FrameTx(nonce: Baseline, nonceKeys: null), false).SetName("frame transaction on the account nonce is free");
        yield return new TestCaseData(() => FrameTx(nonce: Baseline, nonceKeys: [UInt256.Zero]), false).SetName("frame transaction aliasing the account nonce is free");
        yield return new TestCaseData(() => Build.A.Transaction.WithNonce(Baseline).WithSenderAddress(Sender).TestObject, false).SetName("plain transaction is free");
    }

    [Test]
    public void SenderWidthCache_EarnThenSpendIsExact()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, 50_000);
        cache.Earn(Sender, 10_000);

        Assert.That(cache.TrySpend(Sender, Cost), Is.True);
        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)39_000));

        Assert.That(cache.TrySpend(Sender, 39_001), Is.False);
        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)39_000), "a failed spend leaves the width untouched");

        Assert.That(cache.TrySpend(Sender, 39_000), Is.True);
        Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero));
        Assert.That(cache.TrySpend(Sender, 1), Is.False);
    }

    [Test]
    public void SenderWidthCache_RefundRestoresSpentWidthOnce()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, Cost);
        Assert.That(cache.TrySpend(Sender, Cost), Is.True);
        cache.RecordCharge(TestItem.KeccakA.ValueHash256, Sender, Cost);

        cache.RefundCharge(TestItem.KeccakA.ValueHash256);
        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)Cost));

        cache.RefundCharge(TestItem.KeccakA.ValueHash256);
        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)Cost), "a second refund of the same charge is a no-op");
    }

    [Test]
    public void SenderWidthCache_ZeroAmountsAreNoOps()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, UInt256.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero));
            Assert.That(cache.TrySpend(Sender, UInt256.Zero), Is.True);
        }
    }

    [Test]
    public void SenderWidthCache_EarnSaturatesInsteadOfWrapping()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, UInt256.MaxValue);
        cache.Earn(Sender, UInt256.One);

        Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.MaxValue));
    }

    private static AcceptTxResult Accept(SenderWidthCache cache, Transaction tx, TxDistinctSortedPool pending, bool enabled = true)
    {
        TxPoolConfig config = new() { FrameTxWidthEnabled = enabled, FrameTxWidthCostPerAdmission = Cost, MaxPendingTxsPerSender = Baseline };
        FrameTxWidthFilter filter = new(config, pending, Pool(), cache, LimboLogs.Instance.GetClassLogger<FrameTxWidthFilterTests>());
        TxFilteringState filteringState = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    private static TxDistinctSortedPool PendingKeyedTxs(int count)
    {
        Transaction[] pending = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            pending[i] = KeyedTx(nonceSeq: (ulong)i);
        }

        return Pool(pending);
    }

    /// <summary>The real pool type, so the bucket count and the replacement walk run as wired.</summary>
    private static TxDistinctSortedPool Pool(params Transaction[] pending)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new ReleaseSpec { IsEip1559Enabled = false });
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);

        IComparer<Transaction> comparer = new TransactionComparerProvider(specProvider, blockTree).GetDefaultComparer();
        TxDistinctSortedPool pool = new(pending.Length + 1, comparer, LimboLogs.Instance);
        foreach (Transaction tx in pending)
        {
            pool.TryInsert(tx.Hash!, tx);
        }

        return pool;
    }

    private static Transaction KeyedTx(ulong nonceSeq, uint gasPrice = 1) => FrameTx(nonceSeq, [NonceKey], gasPrice);

    private static Transaction FrameTx(ulong nonce, UInt256[]? nonceKeys, uint gasPrice = 1)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = Sender,
            Nonce = nonce,
            NonceKeys = nonceKeys,
            Frames = [],
            FrameSignatures = [],
            GasLimit = 1_000_000,
            GasPrice = gasPrice,
            DecodedMaxFeePerGas = gasPrice,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
