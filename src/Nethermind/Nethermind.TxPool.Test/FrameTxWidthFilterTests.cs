// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.TxPool.Test;

public class FrameTxWidthFilterTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Paymaster = TestItem.AddressB;
    private static readonly UInt256 NonceKey = 0xbeef;
    private const ulong Baseline = 1;
    private const ulong SafetyFactorPermille = 1000;

    private const ulong Cost = (ulong)Eip8141Constants.IntrinsicGasCost + Eip8141Constants.Secp256k1VerificationGasCost + GasCostOf.ColdSLoad;

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
        cache.Earn(Sender, Cost + 2 * Eip8141Constants.Secp256k1VerificationGasCost);

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: Baseline, signatures: 3), PendingKeyedTxs((int)Baseline));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero), "two more signatures add two signature verifications to the admission gas");
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

    [TestCase(true, true, true, TestName = "fee bump of the pending baseline")]
    [TestCase(true, false, false, TestName = "fee bump of a pending transaction that is not the baseline")]
    [TestCase(false, true, false, TestName = "next sequence from the same sender")]
    public void Accept_OnlyReplacingTheBaselineIsFree(bool replaces, bool pendingIsBaseline, bool accepted)
    {
        SenderWidthCache cache = new();
        Transaction incoming = KeyedTx(nonceSeq: replaces ? Baseline - 1 : Baseline, gasPrice: 2);
        Transaction pending = KeyedTx(nonceSeq: Baseline - 1);

        AcceptTxResult result = Accept(cache, incoming, FrameTxFilterTestPools.Pool(false, pending), baseline: pendingIsBaseline ? pending : null);

        Assert.That(result, Is.EqualTo(accepted ? AcceptTxResult.Accepted : AcceptTxResult.WidthUnmet));
    }

    [Test]
    public void Accept_BeyondBaseline_BelowTheNextBaseFee_SpendsNoWidth([Values] bool replaces, [Values] bool atNextBaseFee)
    {
        const uint nextBaseFee = 3;
        SenderWidthCache cache = new();
        cache.Earn(Sender, Cost);
        Transaction incoming = KeyedTx(nonceSeq: replaces ? Baseline - 1 : Baseline, gasPrice: atNextBaseFee ? nextBaseFee : nextBaseFee - 1);

        AcceptTxResult result = Accept(cache, incoming, PendingKeyedTxs((int)Baseline), nextBaseFee: nextBaseFee);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(atNextBaseFee ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLow));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)(atNextBaseFee ? 0 : Cost)));
        }
    }

    [Test]
    public void Accept_Baseline_BelowTheNextBaseFee_IsLeftToTheFeeFilters()
    {
        AcceptTxResult result = Accept(new SenderWidthCache(), KeyedTx(nonceSeq: 0), PendingKeyedTxs(0), nextBaseFee: 2);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
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

    [TestCase(50_000ul, 50_000ul, TestName = "a single earn above the cap is held at it")]
    [TestCase(0ul, 90_000ul, TestName = "a zero cap lifts the ceiling")]
    public void SenderWidthCache_FirstEarnHonoursTheCap(ulong widthCap, ulong expected)
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, 90_000, widthCap: widthCap);

        Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)expected));
    }

    [Test]
    public void Accept_PendingNonKeyedTx_DoesNotTakeTheBaseline()
    {
        SenderWidthCache cache = new();

        AcceptTxResult result = Accept(cache, KeyedTx(nonceSeq: 0), FrameTxFilterTestPools.Pool(false, FrameTx(nonce: 0, nonceKeys: null)));

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

    [TestCase(true, true, 0ul, 90_000ul, TestName = "a sponsored transaction credits its paymaster with the receipt gas")]
    [TestCase(true, true, 50_000ul, 50_000ul, TestName = "the paymaster credit is held at the width cap")]
    [TestCase(true, false, 0ul, 0ul, TestName = "a self-paid transaction credits no paymaster")]
    [TestCase(false, true, 0ul, 0ul, TestName = "disabled width credits no paymaster")]
    public void EarnWidthOnFinalization_CreditsThePaymasterOfASponsoredFrameTransaction(bool enabled, bool sponsored, ulong widthCap, ulong expected)
    {
        FrameTxWidthLedger ledger = new(new TxPoolConfig { FrameTxWidthEnabled = enabled, FrameTxWidthCap = widthCap }, LimboLogs.Instance);
        Transaction finalized = FrameTx(nonce: 0, nonceKeys: null, frames: sponsored ? [OnlyVerify(), Pay(Paymaster)] : [SelfVerify()]);

        ledger.EarnWidthOnFinalization(Build.A.Block.WithTransactions(finalized).TestObject, [new TxReceipt { GasUsed = 90_000 }]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ledger.PaymasterWidth.GetWidth(Paymaster), Is.EqualTo((UInt256)expected));
            Assert.That(ledger.PaymasterWidth.Count, Is.EqualTo(expected == 0 ? 0 : 1));
            Assert.That(ledger.SenderWidth.Count, Is.Zero, "paymaster width is held apart from sender width");
        }
    }

    [Test]
    public void Sponsored_DisabledWidth_KeepsThePaymasterCapAndLeavesTheLedgerUntouched()
    {
        SponsoredAdmission admission = new(enabled: false);
        Transaction second = SponsoredTx(nonce: 1);
        UInt256 earned = ChargeOf(second) * 2;
        admission.PaymasterWidth.Earn(Paymaster, earned);

        AcceptTxResult first = admission.Submit(SponsoredTx(nonce: 0));
        AcceptTxResult beyondTheCap = admission.Submit(second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(beyondTheCap, Is.EqualTo(AcceptTxResult.NonCanonicalPaymasterLimitReached));
            Assert.That(admission.PaymasterWidth.GetWidth(Paymaster), Is.EqualTo(earned));
        }
    }

    [TestCase(null, false, TestName = "paymaster with zero width")]
    [TestCase(-1, false, TestName = "paymaster width one short of the cost")]
    [TestCase(0, true, TestName = "paymaster width exactly covering the cost")]
    [TestCase(1, true, TestName = "paymaster width above the cost")]
    public void Sponsored_BeyondThePaymasterBaseline_SpendsPaymasterWidth(int? widthAboveCost, bool accepted)
    {
        SponsoredAdmission admission = new();
        Transaction additional = SponsoredTx(nonce: 1);
        UInt256 cost = ChargeOf(additional);
        UInt256 earned = widthAboveCost is int above ? (UInt256)(ulong)((long)(ulong)cost + above) : UInt256.Zero;
        admission.PaymasterWidth.Earn(Paymaster, earned);
        admission.SenderWidth.Earn(Sender, cost);

        AcceptTxResult baseline = admission.Submit(SponsoredTx(nonce: 0));
        AcceptTxResult result = admission.Submit(additional);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cost, Is.Not.EqualTo(UInt256.Zero));
            Assert.That(baseline, Is.EqualTo(AcceptTxResult.Accepted), "the single transaction the EIP-8141 cap allows is free");
            Assert.That(result, Is.EqualTo(accepted ? AcceptTxResult.Accepted : AcceptTxResult.PaymasterWidthUnmet));
            Assert.That(admission.PaymasterWidth.GetWidth(Paymaster), Is.EqualTo(accepted ? earned - cost : earned), "a rejection spends nothing");
            Assert.That(admission.SenderWidth.GetWidth(Sender), Is.EqualTo(cost), "a transaction on the account nonce spends no sender width");
            Assert.That(admission.Paymasters.GetPendingCount(Paymaster), Is.EqualTo(accepted ? 2 : 1));
        }
    }

    [Test]
    public void Sponsored_SpentPaymasterWidthIsNotReturned_AndTheBaselineIsNotInherited()
    {
        SponsoredAdmission admission = new();
        Transaction baseline = SponsoredTx(nonce: 0);
        Transaction additional = SponsoredTx(nonce: 1);
        Transaction next = SponsoredTx(nonce: 2);
        admission.PaymasterWidth.Earn(Paymaster, ChargeOf(additional));
        Assert.That(admission.Submit(baseline), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(admission.Submit(additional), Is.EqualTo(AcceptTxResult.Accepted));

        admission.Remove(baseline);
        AcceptTxResult whileOneIsPending = admission.Submit(next);
        admission.Remove(additional);
        UInt256 widthAfterRemoval = admission.PaymasterWidth.GetWidth(Paymaster);
        AcceptTxResult onceNoneIsPending = admission.Submit(next);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileOneIsPending, Is.EqualTo(AcceptTxResult.PaymasterWidthUnmet), "the baseline that left is not handed to another pending transaction");
            Assert.That(widthAfterRemoval, Is.EqualTo(UInt256.Zero), "removal returns no width");
            Assert.That(onceNoneIsPending, Is.EqualTo(AcceptTxResult.Accepted));
        }
    }

    [TestCase(true, TestName = "replacing the paymaster baseline is free")]
    [TestCase(false, TestName = "replacing a transaction beyond the paymaster baseline spends again")]
    public void Sponsored_OnlyReplacingThePaymasterBaselineIsFree(bool replacesBaseline)
    {
        SponsoredAdmission admission = new();
        Transaction additional = SponsoredTx(nonce: 1);
        UInt256 cost = ChargeOf(additional);
        admission.PaymasterWidth.Earn(Paymaster, cost);
        Assert.That(admission.Submit(SponsoredTx(nonce: 0)), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(admission.Submit(additional), Is.EqualTo(AcceptTxResult.Accepted));
        ulong replacedNonce = replacesBaseline ? 0ul : 1ul;

        AcceptTxResult withoutWidth = admission.Submit(SponsoredTx(replacedNonce, gasPrice: 2));
        admission.PaymasterWidth.Earn(Paymaster, cost);
        AcceptTxResult withWidth = admission.Submit(SponsoredTx(replacedNonce, gasPrice: 3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutWidth, Is.EqualTo(replacesBaseline ? AcceptTxResult.Accepted : AcceptTxResult.PaymasterWidthUnmet));
            Assert.That(withWidth, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(admission.PaymasterWidth.GetWidth(Paymaster), Is.EqualTo(replacesBaseline ? cost : UInt256.Zero), "a replacement of the baseline stays the baseline");
            Assert.That(admission.Paymasters.GetPendingCount(Paymaster), Is.EqualTo(2));
        }
    }

    [Test]
    public void Sponsored_BeyondThePaymasterBaseline_RefusedBeforeAnySpend([Values] bool underbidsAPendingOne)
    {
        const uint nextBaseFee = 3;
        SponsoredAdmission admission = new(nextBaseFee: nextBaseFee);
        Transaction additional = SponsoredTx(nonce: 1, gasPrice: nextBaseFee + 1);
        UInt256 earned = ChargeOf(additional) * 2;
        admission.PaymasterWidth.Earn(Paymaster, earned);
        Assert.That(admission.Submit(SponsoredTx(nonce: 0, gasPrice: nextBaseFee)), Is.EqualTo(AcceptTxResult.Accepted));
        if (underbidsAPendingOne) Assert.That(admission.Submit(additional), Is.EqualTo(AcceptTxResult.Accepted));
        UInt256 before = admission.PaymasterWidth.GetWidth(Paymaster);

        AcceptTxResult result = admission.Submit(SponsoredTx(nonce: 1, gasPrice: underbidsAPendingOne ? nextBaseFee : nextBaseFee - 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(underbidsAPendingOne ? AcceptTxResult.ReplacementNotAllowed : AcceptTxResult.FeeTooLow));
            Assert.That(admission.PaymasterWidth.GetWidth(Paymaster), Is.EqualTo(before));
            Assert.That(admission.Paymasters.GetPendingCount(Paymaster), Is.EqualTo(underbidsAPendingOne ? 2 : 1), "the refused admission holds no paymaster slot");
        }
    }

    [Test]
    public void Sponsored_BeyondBothBaselines_SpendsSenderWidthFirst()
    {
        SponsoredAdmission admission = new();
        Transaction additional = SponsoredTx(nonce: 1, nonceKeys: [NonceKey]);
        UInt256 cost = ChargeOf(additional);
        admission.PaymasterWidth.Earn(Paymaster, cost);
        Assert.That(admission.Submit(SponsoredTx(nonce: 0, nonceKeys: [NonceKey])), Is.EqualTo(AcceptTxResult.Accepted));

        AcceptTxResult senderWithoutWidth = admission.Submit(additional);
        UInt256 paymasterWidthAfterRefusal = admission.PaymasterWidth.GetWidth(Paymaster);
        admission.SenderWidth.Earn(Sender, cost);
        AcceptTxResult senderWithWidth = admission.Submit(additional);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(senderWithoutWidth, Is.EqualTo(AcceptTxResult.WidthUnmet));
            Assert.That(paymasterWidthAfterRefusal, Is.EqualTo(cost), "a sender without width cannot spend its paymaster's");
            Assert.That(senderWithWidth, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(admission.SenderWidth.GetWidth(Sender), Is.EqualTo(UInt256.Zero));
            Assert.That(admission.PaymasterWidth.GetWidth(Paymaster), Is.EqualTo(UInt256.Zero), "a transaction beyond both baselines spends both");
        }
    }

    [Test]
    public void Sponsored_PaymasterDrainedDuringThePrefixSimulation_SpendsNoSenderWidth()
    {
        SponsoredAdmission admission = new();
        Transaction additional = SponsoredTx(nonce: 1, nonceKeys: [NonceKey]);
        UInt256 cost = ChargeOf(additional);
        admission.PaymasterWidth.Earn(Paymaster, cost);
        admission.SenderWidth.Earn(Sender, cost);
        Assert.That(admission.Submit(SponsoredTx(nonce: 0, nonceKeys: [NonceKey])), Is.EqualTo(AcceptTxResult.Accepted));

        AcceptTxResult result = admission.Submit(additional, afterThePaymasterFilter: () => admission.PaymasterWidth.TrySpend(Paymaster, 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.PaymasterWidthUnmet));
            Assert.That(admission.SenderWidth.GetWidth(Sender), Is.EqualTo(cost));
            Assert.That(admission.Paymasters.GetPendingCount(Paymaster), Is.EqualTo(1));
        }
    }

    private static AcceptTxResult Accept(SenderWidthCache cache, Transaction tx, TxDistinctSortedPool pending, bool enabled = true, ulong permille = SafetyFactorPermille, Transaction? baseline = null, uint nextBaseFee = 0)
    {
        TxPoolConfig config = new() { FrameTxWidthEnabled = enabled, FrameTxWidthSafetyFactorPermille = permille };
        ConcurrentDictionary<AddressAsKey, ValueHash256> baselines = new();
        if (baseline is not null) baselines[Sender] = baseline.Hash!.ValueHash256;
        FrameTxWidthFilter filter = new(config, new TestChainHeadInfoProvider { NextBaseFee = nextBaseFee }, pending, FrameTxFilterTestPools.Pool(false), cache, baselines, new SenderWidthCache(holdsPaymasters: true), LimboLogs.Instance.GetClassLogger<FrameTxWidthFilterTests>());
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

        return FrameTxFilterTestPools.Pool(false, pending);
    }

    private static Transaction KeyedTx(ulong nonceSeq, uint gasPrice = 1, int signatures = 1) => FrameTx(nonceSeq, [NonceKey], gasPrice, signatures);

    private static Transaction FrameTx(ulong nonce, UInt256[]? nonceKeys, uint gasPrice = 1, int signatures = 1, TxFrame[]? frames = null)
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
            Frames = frames ?? [],
            FrameSignatures = frameSignatures,
            GasLimit = 1_000_000,
            GasPrice = gasPrice,
            DecodedMaxFeePerGas = gasPrice,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static Transaction SponsoredTx(ulong nonce, UInt256[]? nonceKeys = null, uint gasPrice = 1) =>
        FrameTx(nonce, nonceKeys, gasPrice, frames: [OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)]);

    private static UInt256 ChargeOf(Transaction tx) => FrameTxWidthCharge.For(tx, Eip8141Prototype.Instance, SafetyFactorPermille);

    /// <summary>The paymaster filter and the width filter as the pool chains them, over the pending set they admitted.</summary>
    private sealed class SponsoredAdmission(bool enabled = true, uint nextBaseFee = 0)
    {
        private readonly TxPoolConfig _config = new() { FrameTxWidthEnabled = enabled };
        private readonly ConcurrentDictionary<AddressAsKey, ValueHash256> _senderBaselines = new();
        private readonly ConcurrentDictionary<AddressAsKey, ValueHash256> _paymasterBaselines = new();
        private readonly List<Transaction> _pending = [];

        public PendingPaymasterCache Paymasters { get; } = new();

        public SenderWidthCache SenderWidth { get; } = new();

        public SenderWidthCache PaymasterWidth { get; } = new(holdsPaymasters: true);

        public AcceptTxResult Submit(Transaction tx, Action? afterThePaymasterFilter = null)
        {
            TestReadOnlyStateProvider chain = new();
            chain.InsertCode([0x60, 0x00], Paymaster);
            TxDistinctSortedPool pool = FrameTxFilterTestPools.Pool(false, [.. _pending]);
            TxDistinctSortedPool blobPool = FrameTxFilterTestPools.Pool(true);
            ILogger logger = LimboLogs.Instance.GetClassLogger<FrameTxWidthFilterTests>();
            FrameTxPaymasterFilter paymasterFilter = new(chain, pool, blobPool, Paymasters, _config, PaymasterWidth, _paymasterBaselines, logger);
            FrameTxWidthFilter widthFilter = new(_config, new TestChainHeadInfoProvider { NextBaseFee = nextBaseFee }, pool, blobPool, SenderWidth, _senderBaselines, PaymasterWidth, logger);
            TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);

            AcceptTxResult result = paymasterFilter.Accept(tx, ref state, TxHandlingOptions.None);
            if (result)
            {
                afterThePaymasterFilter?.Invoke();
                result = widthFilter.Accept(tx, ref state, TxHandlingOptions.None);
            }

            if (!result)
            {
                if (state.PaymasterReserved) Paymasters.Decrement(Paymaster);
                return result;
            }

            if (PendingReplacement.Find(tx, pool, blobPool) is Transaction replaced) Remove(replaced);
            _pending.Add(tx);
            if (state.TakesSenderBaseline) _senderBaselines[Sender] = tx.Hash!.ValueHash256;
            if (state.TakesPaymasterBaseline) _paymasterBaselines[Paymaster] = tx.Hash!.ValueHash256;
            return result;
        }

        public void Remove(Transaction tx)
        {
            _pending.Remove(tx);
            Paymasters.Decrement(Paymaster);
            _senderBaselines.TryRemove(new KeyValuePair<AddressAsKey, ValueHash256>(Sender, tx.Hash!.ValueHash256));
            _paymasterBaselines.TryRemove(new KeyValuePair<AddressAsKey, ValueHash256>(Paymaster, tx.Hash!.ValueHash256));
        }
    }
}
