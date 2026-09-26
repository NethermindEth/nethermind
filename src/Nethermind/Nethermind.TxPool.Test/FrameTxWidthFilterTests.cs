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

public class FrameTxWidthFilterTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly UInt256 NonceKey = 0xbeef;
    private const ulong Baseline = 1;
    private const ulong SafetyFactorPermille = 1000;

    private const ulong Cost = Eip8141Constants.Secp256k1VerificationGasCost;

    [TestCase(0, true, TestName = "the single baseline admission is free")]
    [TestCase((int)Baseline, false, TestName = "first admission beyond the baseline")]
    [TestCase((int)Baseline + 1, false, TestName = "further beyond the baseline")]
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

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: Baseline), PendingKeyedTxs((int)Baseline));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(accepted ? AcceptTxResult.Accepted : AcceptTxResult.WidthUnmet));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)(accepted ? earned - Cost : earned)), "a rejection spends nothing");
        }
    }

    [TestCase(1000ul, Cost, TestName = "unit safety factor charges the admission gas")]
    [TestCase(2000ul, Cost * 2, TestName = "double safety factor charges twice the admission gas")]
    public void Accept_ChargeScalesWithSafetyFactor(ulong permille, ulong charge)
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, charge);

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: Baseline), PendingKeyedTxs((int)Baseline), permille: permille);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero), "the charge scaled by the safety factor drained the earned width");
        }
    }

    [Test]
    public void Accept_ChargeScalesWithAdmissionGas()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, Cost * 3);

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: Baseline, signatures: 3), PendingKeyedTxs((int)Baseline));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero), "three signatures cost three times one signature's admission gas");
        }
    }

    [Test]
    public void Accept_ChargedAdmission_SpendsWidthPermanently()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, Cost);
        Transaction tx = KeyedTx(nonceSeq: Baseline);

        AcceptTxResult admitted = Accept(cache, tx, PendingKeyedTxs((int)Baseline));
        AcceptTxResult readmitted = Accept(cache, tx, PendingKeyedTxs((int)Baseline));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(admitted, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(readmitted, Is.EqualTo(AcceptTxResult.WidthUnmet), "spent width is not returned, so re-admission needs re-earning");
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero));
        }
    }

    [TestCase(true, true, TestName = "fee bump of a pending keyed transaction")]
    [TestCase(false, false, TestName = "next sequence from the same sender")]
    public void Accept_ReplacementIsNotAnAdditionalAdmission(bool replaces, bool accepted)
    {
        SenderWidthCache cache = new();
        Transaction incoming = KeyedTx(nonceSeq: replaces ? Baseline - 1 : Baseline, gasPrice: 2);

        AcceptTxResult result = Accept(cache, incoming, PendingKeyedTxs((int)Baseline));

        Assert.That(result, Is.EqualTo(accepted ? AcceptTxResult.Accepted : AcceptTxResult.WidthUnmet));
    }

    [TestCase(true, TestName = "width enabled")]
    [TestCase(false, TestName = "width disabled")]
    public void Accept_DisabledWidth_NeverRejectsOrTouchesTheLedger(bool enabled)
    {
        SenderWidthCache cache = new();
        long rejectionsBefore = Metrics.PendingTransactionsFrameTxWidthUnmet;

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: Baseline), PendingKeyedTxs((int)Baseline), enabled);

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

        AcceptTxResult result = Accept(cache, build(), PendingKeyedTxs((int)Baseline));

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

        Assert.That(cache.TrySpend(Sender, 21_000), Is.True);
        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)39_000));

        Assert.That(cache.TrySpend(Sender, 39_001), Is.False);
        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)39_000), "a failed spend leaves the width untouched");

        Assert.That(cache.TrySpend(Sender, 39_000), Is.True);
        Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero));
        Assert.That(cache.TrySpend(Sender, 1), Is.False);
    }

    [Test]
    public void SenderWidthCache_SpentWidthIsNotReturned()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, 21_000);
        Assert.That(cache.TrySpend(Sender, 21_000), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero), "the spend drained the balance");
            Assert.That(cache.TrySpend(Sender, 21_000), Is.False, "nothing credits spent width back");
        }
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

    [Test]
    public void SenderWidthCache_EarnHoldsAtTheCap()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, 30_000, widthCap: 50_000);
        cache.Earn(Sender, 30_000, widthCap: 50_000);

        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)50_000), "earned width never rises above the cap");
    }

    [Test]
    public void SenderWidthCache_FirstEarnClampsToTheCap()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, 90_000, widthCap: 50_000);

        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)50_000), "a single earn above the cap is held at it");
    }

    [Test]
    public void SenderWidthCache_ZeroCapLiftsTheCeiling()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, 90_000, widthCap: 0);

        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)90_000), "a zero cap lifts the ceiling");
    }

    [Test]
    public void Accept_PendingNonKeyedTx_DoesNotTakeTheBaseline()
    {
        SenderWidthCache cache = new();

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: 0), Pool(FrameTx(nonce: 0, nonceKeys: null)));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_SafetyFactorBelowOne_ChargesTheAdmissionGas()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, Cost - 1);

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: Baseline), PendingKeyedTxs((int)Baseline), permille: 0);

        Assert.That(result, Is.EqualTo(AcceptTxResult.WidthUnmet));
    }

    [Test]
    public void SenderWidthCache_LoweredCapKeepsEarnedWidth()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, 90_000, widthCap: 0);
        cache.Earn(Sender, 30_000, widthCap: 50_000);

        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)90_000));
    }

    private static AcceptTxResult Accept(SenderWidthCache cache, Transaction tx, TxDistinctSortedPool pending, bool enabled = true, ulong permille = SafetyFactorPermille)
    {
        TxPoolConfig config = new() { FrameTxWidthEnabled = enabled, FrameTxWidthSafetyFactorPermille = permille, MaxPendingTxsPerSender = (int)Baseline };
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

    private static Transaction KeyedTx(ulong nonceSeq, uint gasPrice = 1, int signatures = 1) => FrameTx(nonceSeq, [NonceKey], gasPrice, signatures);

    private static Transaction FrameTx(ulong nonce, UInt256[]? nonceKeys, uint gasPrice = 1, int signatures = 1)
    {
        TxFrameSignature[] frameSignatures = new TxFrameSignature[signatures];
        for (int i = 0; i < signatures; i++)
        {
            frameSignatures[i] = new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, default);
        }

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = Sender,
            Nonce = nonce,
            NonceKeys = nonceKeys,
            Frames = [],
            FrameSignatures = frameSignatures,
            GasLimit = 1_000_000,
            GasPrice = gasPrice,
            DecodedMaxFeePerGas = gasPrice,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
