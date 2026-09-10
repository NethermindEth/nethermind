// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.TxPool.Test.FrameTxFilterTestPools;

namespace Nethermind.TxPool.Test;

/// <summary>EIP-8141 per-payer exposure gate: a frame tx is rejected once its payer's summed pending max cost
/// would exceed the payer's balance.</summary>
public class FrameTxPayerExposureFilterTests
{
    private static readonly Address Payer = TestItem.AddressB;
    private static readonly IReleaseSpec Spec = Eip8141Prototype.Instance;
    private const int TestCost = 100_000;
    private const int OrdinaryCost = 21_000;

    // The bound is inclusive: a tx whose reserved + max_cost exactly equals the balance is admitted,
    // matching the spec's strict `available < tx.max_cost` rejection condition.
    [TestCase(TestCost, 0, false, TestName = "single tx within balance")]
    [TestCase(TestCost - 1, 0, true, TestName = "single tx over balance")]
    [TestCase(TestCost + 50_000 - 1, 50_000, true, TestName = "summed exposure over balance")]
    [TestCase(TestCost + 50_000, 50_000, false, TestName = "summed exposure at inclusive boundary")]
    public void Accept_GatesOnPayerExposure(int balance, int reserved, bool rejected)
    {
        TestReadOnlyStateProvider state = StateWithPayerBalance(balance);
        PayerExposureCache cache = new();
        if (reserved > 0) cache.TryReserve(Payer, HashFor(1), (UInt256)reserved, UInt256.MaxValue, out _);

        AcceptTxResult result = Accept(state, cache, FrameTxCostingExactly(TestCost));

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.FrameTxPayerExposureExceeded : AcceptTxResult.Accepted));
    }

    // Pricing the bound on the gas leg alone would let a frame tx name blob hashes at an arbitrary
    // max_fee_per_blob_gas and hold exposure the bound never counted.
    [TestCase(1, 3, TestName = "one blob")]
    [TestCase(2, 5, TestName = "two blobs")]
    [TestCase(6, 1_000_000, TestName = "six blobs at a realistic blob fee")]
    public void Accept_BlobCarryingFrameTx_ReservesTheBlobTermToo(int blobCount, int maxFeePerBlobGas)
    {
        // long: the product exceeds int at six blobs and a realistic blob fee.
        long blobTerm = (long)Eip4844Constants.GasPerBlob * blobCount * maxFeePerBlobGas;
        PayerExposureCache cache = new();

        AcceptTxResult atBound = Accept(StateWithPayerBalance(TestCost + blobTerm), cache, BlobFrameTx(blobCount, maxFeePerBlobGas));
        AcceptTxResult oneWeiShort = Accept(StateWithPayerBalance(TestCost + blobTerm - 1), new PayerExposureCache(), BlobFrameTx(blobCount, maxFeePerBlobGas));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atBound, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)(TestCost + blobTerm)), "the gas leg and the whole blob term are reserved");
            Assert.That(oneWeiShort, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded));
        }
    }

    // The gate runs before AddCore resolves the replacement, so the displaced tx is still reserved; only a tx
    // this one displaces — same sender, same nonce, same payer — may be discounted from the bound.
    [TestCase(0ul, false, false, TestName = "a fee bump discounts the tx it displaces")]
    [TestCase(1ul, false, true, TestName = "a later nonce joins the pending set instead")]
    [TestCase(0ul, true, true, TestName = "an incumbent paid by another payer frees that one")]
    public void Accept_DiscountsOnlyTheReservationItDisplaces(ulong bumpNonce, bool incumbentPaidByAnother, bool rejected)
    {
        const int incumbentCost = TestCost;
        const int bumpCost = TestCost + TestCost / 2;
        const int balance = 2 * TestCost;

        Transaction incumbent = FrameTxCostingExactly(incumbentCost, payer: incumbentPaidByAnother ? TestItem.AddressC : null);
        incumbent.Hash = TestItem.KeccakA;
        // Already pending, so it carries the reservation admission recorded on it; that is what a bump discounts.
        incumbent.PayerExposure = incumbentCost;
        Transaction bump = FrameTxCostingExactly(bumpCost);
        bump.Nonce = bumpNonce;
        bump.Hash = TestItem.KeccakB;

        // The two summed exceed the balance, so only discounting the displaced incumbent admits the bump.
        // Case three's reservation sits with the incumbent's own payer, and Payer's comes from elsewhere.
        PayerExposureCache cache = new();
        cache.TryReserve(incumbent.PayerAddress!, incumbent.Hash!, incumbentCost, balance: balance, out _);
        if (incumbentPaidByAnother) cache.TryReserve(Payer, HashFor(9), incumbentCost, balance: balance, out _);

        AcceptTxResult result = Accept(StateWithPayerBalance(balance), cache, bump, pending: Pool(blobs: false, incumbent));

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.FrameTxPayerExposureExceeded : AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_DoesNotDiscountAnIncumbentWhoseReservationWasAlreadyReleased()
    {
        // Find and the reservation are two operations. Pinned here at the point between them: the incumbent
        // is still what the walk returns, but its reservation has gone and another transaction took the room.
        // Discounting it a second time would leave the payer holding twice its balance.
        const int cost = TestCost;
        const int balance = TestCost;

        Transaction incumbent = FrameTxCostingExactly(cost);
        incumbent.Hash = TestItem.KeccakA;
        incumbent.PayerExposure = cost;
        Transaction bump = FrameTxCostingExactly(cost);
        bump.Hash = TestItem.KeccakB;

        PayerExposureCache cache = new();
        cache.TryReserve(Payer, incumbent.Hash, cost, balance: balance, out _);
        cache.Subtract(incumbent.Hash);
        cache.TryReserve(Payer, TestItem.KeccakC, cost, balance: balance, out _);

        AcceptTxResult result = Accept(StateWithPayerBalance(balance), cache, bump, pending: Pool(blobs: false, incumbent));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded));
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)cost), "the payer never holds more than its balance");
            Assert.That(bump.PayerExposure, Is.Null, "a rejected tx must not claim a reservation it never took");
        }
    }

    [Test]
    public void Accept_DiscountsALightRecordIncumbent()
    {
        // At the shipped blob mode the incumbent is a frameless light record, which cannot be priced, so
        // the discount has to read the reservation the record carries or a fee bump is refused exposure
        // it no longer owes.
        const int incumbentCost = TestCost;
        const int bumpCost = TestCost + TestCost / 2;
        const int balance = 2 * TestCost;

        // The two summed exceed the balance, so only discounting the displaced record admits the bump.
        Transaction incumbent = BlobFrameTxCosting(incumbentCost);
        incumbent.Hash = TestItem.KeccakA;
        incumbent.PayerAddress = Payer;
        incumbent.PayerExposure = incumbentCost;
        Transaction record = new LightTransaction(incumbent);

        Transaction bump = BlobFrameTxCosting(bumpCost);
        bump.Hash = TestItem.KeccakB;

        PayerExposureCache cache = new();
        cache.TryReserve(Payer, record.Hash!, incumbentCost, balance: balance, out _);

        AcceptTxResult result = Accept(StateWithPayerBalance(balance), cache, bump, pending: Pool(blobs: true, record));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Frames, Is.Null, "the incumbent must be frameless, or this pins nothing");
            Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
        }
    }

    [Test]
    public void Accept_ReservesOnAdmission_SoASecondTxFromOnePayerSeesIt()
    {
        TestReadOnlyStateProvider state = StateWithPayerBalance(TestCost + TestCost / 2);
        PayerExposureCache cache = new();

        Transaction admitted = FrameTxCostingExactly(TestCost);
        Transaction turnedAway = FrameTxCostingExactly(TestCost);

        AcceptTxResult first = Accept(state, cache, admitted);
        AcceptTxResult second = Accept(state, cache, turnedAway);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(second, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded));
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)TestCost), "only the admitted tx is reserved");
            Assert.That(admitted.PayerExposure, Is.EqualTo((UInt256)TestCost), "the admitted tx carries what it reserved, so its removal releases the same");
            Assert.That(turnedAway.PayerExposure, Is.Null, "a rejected tx must not claim a reservation it never took");
        }
    }

    [TestCase(TestCost, false, TestName = "the sender covers the max cost")]
    [TestCase(TestCost - 1, true, TestName = "the sender falls short")]
    public void Accept_UnresolvedFramePayer_GatesTheSenderAndReservesNothing(int senderBalance, bool rejected)
    {
        // FrameTxPayerFilter left the payer null (RequiresSimulation with no verdict). Nothing can be
        // reserved, but the sibling balance filters skip frame txs, so this is the only gate left.
        Transaction tx = FrameTxCostingExactly(TestCost);
        tx.PayerAddress = null;
        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)senderBalance);
        PayerExposureCache cache = new();

        AcceptTxResult result = Accept(StateWithPayerBalance(0), cache, tx, senderAccounts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.InsufficientFunds : AcceptTxResult.Accepted),
                "the sender is simply underfunded, which is not a sponsor over-exposure");
            Assert.That(cache.GetReserved(TestItem.AddressA), Is.EqualTo(UInt256.Zero), "an unresolved payer names no account to reserve against");
            Assert.That(tx.PayerExposure, Is.EqualTo(rejected ? null : (UInt256?)TestCost),
                "an admitted one still carries its price, which is what the sender bound sums it at next time");
        }
    }

    // Each was admitted on its own count: BalanceTooLowFilter's cumulative walk skips frame txs, and a
    // payer-less one reserves nothing, so one balance would admit an unbounded run of them.
    [TestCase(2 * TestCost, false, TestName = "the sender covers both")]
    [TestCase(2 * TestCost - 1, true, TestName = "the sender covers only one")]
    public void Accept_PayerlessFrameTx_SumsTheSendersOtherPayerlessPending(int senderBalance, bool rejected)
    {
        Transaction pending = FrameTxCostingExactly(TestCost);
        pending.PayerAddress = null;
        pending.PayerExposure = TestCost; // as its own admission priced it
        pending.Hash = TestItem.KeccakA;

        Transaction tx = FrameTxCostingExactly(TestCost);
        tx.PayerAddress = null;
        tx.Nonce = 1;
        tx.Hash = TestItem.KeccakB;

        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)senderBalance);

        AcceptTxResult result = Accept(StateWithPayerBalance(0), new PayerExposureCache(), tx, senderAccounts, Pool(blobs: false, pending));

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.InsufficientFunds : AcceptTxResult.Accepted));
    }

    // The mirror of the case below: the walk skips every transaction with a resolved payer, and the ledger
    // covers that set, so a payer-less arrival — reserving nothing — has to read the ledger itself.
    [TestCase(1ul, 2 * TestCost, false, TestName = "the sender covers the reservation and the arrival")]
    [TestCase(1ul, 2 * TestCost - 1, true, TestName = "the sender covers only the reservation")]
    [TestCase(0ul, TestCost, false, TestName = "a replacement is not measured against what it displaces")]
    public void Accept_PayerlessFrameTx_SumsTheSendersSelfPaidReservation(ulong nonce, int senderBalance, bool rejected)
    {
        Transaction pending = FrameTxCostingExactly(TestCost, payer: TestItem.AddressA);
        pending.PayerExposure = TestCost; // as its own admission priced and reserved it
        pending.Hash = TestItem.KeccakA;

        Transaction tx = FrameTxCostingExactly(TestCost);
        tx.PayerAddress = null;
        tx.Nonce = nonce;
        tx.Hash = TestItem.KeccakB;

        PayerExposureCache cache = new();
        cache.TryReserve(TestItem.AddressA, pending.Hash, TestCost, UInt256.MaxValue, out _);

        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)senderBalance);

        AcceptTxResult result = Accept(StateWithPayerBalance(0), cache, tx, senderAccounts, Pool(blobs: false, pending));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.InsufficientFunds : AcceptTxResult.Accepted));
            Assert.That(cache.GetReserved(TestItem.AddressA), Is.EqualTo((UInt256)TestCost),
                "a payer-less admission takes no reservation of its own");
        }
    }

    [Test]
    public void Accept_PayerlessFrameTx_DoesNotDiscountASelfPaidReservationAlreadyReleased()
    {
        // The payer-less leg reserves nothing, so it discounts the incumbent out of the ledger itself. Pinned
        // between the walk and the release, as the reserving leg is: the incumbent is still what the walk
        // returns, but another transaction has taken the room its reservation held.
        Transaction incumbent = FrameTxCostingExactly(TestCost, payer: TestItem.AddressA);
        incumbent.PayerExposure = TestCost;
        incumbent.Hash = TestItem.KeccakA;

        Transaction tx = FrameTxCostingExactly(TestCost);
        tx.PayerAddress = null;
        tx.Hash = TestItem.KeccakB;

        PayerExposureCache cache = new();
        cache.TryReserve(TestItem.AddressA, incumbent.Hash, TestCost, UInt256.MaxValue, out _);
        cache.Subtract(incumbent.Hash);
        cache.TryReserve(TestItem.AddressA, TestItem.KeccakC, TestCost, UInt256.MaxValue, out _);

        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)TestCost);

        AcceptTxResult result = Accept(StateWithPayerBalance(0), cache, tx, senderAccounts, Pool(blobs: false, incumbent));

        Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds));
    }

    [Test]
    public void Accept_PayerlessFrameTx_SumsARestoredRecordAtTheAdmittedPrice()
    {
        // A restored record is frameless and cannot be re-priced, so without the exposure in its bytes the
        // bound falls back to the gas-limit product, which carries only the frame-gas sum.
        Transaction pending = BlobFrameTxCosting(TestCost);
        pending.PayerAddress = null;
        pending.PayerExposure = TestCost;
        pending.GasLimit = FrameTxValidation.TotalGasLimit(pending.Frames!);
        pending.Hash = TestItem.KeccakA;
        Transaction restored = LightTxDecoder.Decode(LightTxDecoder.Encode(pending));

        Transaction tx = BlobFrameTxCosting(TestCost);
        tx.PayerAddress = null;
        tx.Nonce = 1;
        tx.Hash = TestItem.KeccakB;

        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)(2 * TestCost - 1));

        AcceptTxResult result = Accept(StateWithPayerBalance(0), new PayerExposureCache(), tx, senderAccounts, Pool(blobs: true, restored));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.Frames, Is.Null, "the incumbent must be frameless, or this pins nothing");
            Assert.That((UInt256)pending.GasLimit * pending.MaxFeePerGas, Is.LessThan((UInt256)TestCost),
                "the product must understate the price, or the fallback would reject too");
            Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds));
        }
    }

    [Test]
    public void Accept_PayerlessFrameTx_ChargesARestoredSponsoredRecordTheFallbackPrice()
    {
        // A sponsored record admitted with no payer resolved holds no price, and its paymaster is what makes
        // the record carry the exposure slot at all. Decoded as a zero price it would cost the sender nothing.
        Transaction pending = SponsoredFrameTx();
        pending.PayerExposure = null;
        pending.GasLimit = FrameTxValidation.TotalGasLimit(pending.Frames!);
        pending.Hash = TestItem.KeccakA;
        Transaction restored = LightTxDecoder.Decode(LightTxDecoder.Encode(pending));

        Transaction tx = BlobFrameTxCosting(TestCost);
        tx.PayerAddress = null;
        tx.Nonce = 1;
        tx.Hash = TestItem.KeccakB;

        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)TestCost);

        AcceptTxResult result = Accept(StateWithPayerBalance(0), new PayerExposureCache(), tx, senderAccounts, Pool(blobs: true, restored));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.PersistedPaymaster, Is.Not.Null, "the paymaster is what makes the record carry the slot");
            Assert.That(restored.PayerExposure, Is.Null, "an unpriced record must not read back as a free one");
            Assert.That((UInt256)restored.GasLimit * restored.MaxFeePerGas, Is.GreaterThan(UInt256.Zero),
                "the fallback must charge something, or this pins nothing");
            Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds));
        }
    }

    // The exposure ledger holds frame reservations only, so a plain transaction pooled first is invisible to
    // it and the sender's balance would be booked twice. An EIP-8250 sequence does not order against the
    // account nonce, so that ordinary liability is not the walk's to skip however the two compare numerically.
    [TestCase(TestCost + OrdinaryCost, false, false, TestName = "the sender covers both")]
    [TestCase(TestCost + OrdinaryCost - 1, true, false, TestName = "the sender covers only one")]
    [TestCase(TestCost + OrdinaryCost, false, true, TestName = "a keyed sequence and the sender covers both")]
    [TestCase(TestCost + OrdinaryCost - 1, true, true, TestName = "a keyed sequence and the sender covers only one")]
    public void Accept_SelfPayingFrameTx_SumsTheSendersOrdinaryPending(int senderBalance, bool rejected, bool keyed)
    {
        // A fresh key is current at sequence 0, below the account nonce the ordinary transaction sits at.
        const ulong accountNonce = 7;

        // A legacy transaction, so its cost is exactly gas_limit * gas_price.
        Transaction ordinary = Build.A.Transaction.WithSenderAddress(TestItem.AddressA)
            .WithNonce(keyed ? accountNonce : 0).WithGasLimit(OrdinaryCost).WithGasPrice(1).WithValue(0).TestObject;
        ordinary.Hash = TestItem.KeccakA;

        Transaction tx = FrameTxCostingExactly(TestCost, payer: TestItem.AddressA);
        tx.Nonce = keyed ? 0ul : 1ul;
        if (keyed) tx.NonceKeys = [(UInt256)0xbeef];
        tx.Hash = TestItem.KeccakB;

        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)senderBalance, keyed ? accountNonce : 0);

        AcceptTxResult result = Accept(new TestReadOnlyStateProvider(), new PayerExposureCache(), tx, senderAccounts, Pool(blobs: false, ordinary));

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.InsufficientFunds : AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_UnpriceableMaxCost_DoesNotDisconnectTheRelayingPeer()
    {
        // AcceptTxResult.Invalid is the one result TxFloodController maps to an immediate disconnect, and
        // an unpriceable max_cost is unincludable rather than malformed.
        Transaction tx = FrameTx(0);
        tx.DecodedMaxFeePerGas = UInt256.MaxValue;

        AcceptTxResult result = Accept(StateWithPayerBalance(TestCost), new PayerExposureCache(), tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FrameTxValidation.TryCalculateMaxCost(tx, Spec, out _), Is.False, "the fixture must overflow, or this pins nothing");
            Assert.That(result, Is.EqualTo(AcceptTxResult.Int256Overflow));
            Assert.That(result, Is.Not.EqualTo(AcceptTxResult.Invalid));
        }
    }

    [Test]
    public void Accept_NonFrameTx_PassesThrough()
    {
        Transaction tx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;

        AcceptTxResult result = Accept(StateWithPayerBalance(0), new PayerExposureCache(), tx);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [TestCase(TestCost, false, TestName = "self-paying sender within its balance")]
    [TestCase(TestCost - 1, true, TestName = "self-paying sender over its balance")]
    public void Accept_SelfPayingSender_GatesOnTheAccountTheSiblingBalanceFiltersUsed(int balance, bool rejected)
    {
        // Native resolution only ever yields payer == sender today, so this is the branch every real
        // admission takes: it must read the cached sender account, not the state provider.
        Transaction tx = FrameTxCostingExactly(TestCost, payer: TestItem.AddressA);
        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)balance);

        // The state provider is left empty: reading it instead would see a zero balance and always reject.
        AcceptTxResult result = Accept(new TestReadOnlyStateProvider(), new PayerExposureCache(), tx, senderAccounts);

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.InsufficientFunds : AcceptTxResult.Accepted));
    }

    /// <summary>Where the SENDER frame whose value is bounded sits, relative to the validation prefix.</summary>
    public enum SenderFramePosition
    {
        /// <summary>[self_verify, user_op]: the basic self-relay layout.</summary>
        BehindTheApprovingFrame,

        /// <summary>[deploy, self_verify, user_op]: the deploy-new-account layout, EIP-8141's other self-relay prefix.</summary>
        BehindThePrologueDeployFrame,

        /// <summary>[expiry_verify, deploy, self_verify, user_op]: the same, behind the optional expiry frame.</summary>
        BehindTheExpiryAndDeployFrames,

        /// <summary>[self_verify, DEFAULT, user_op]: a DEFAULT frame past the prefix runs arbitrary code.</summary>
        BehindAPostPrefixDefaultFrame,
    }

    // TXPARAM(0x06) prices gas alone, so the wei a SENDER frame moves sits outside it. One in the leading prologue
    // needs the balance to already hold it; one behind a DEFAULT frame the prefix does not cover may be funded by
    // what that frame does. Payer-less and self-paid alike: neither leg reserves the sender's value anywhere else.
    [TestCase(0, false, SenderFramePosition.BehindTheApprovingFrame, false, TestName = "the sender covers the fee and the leading frame's value")]
    [TestCase(-1, true, SenderFramePosition.BehindTheApprovingFrame, false, TestName = "the sender falls one wei short of the leading frame's value")]
    [TestCase(0, false, SenderFramePosition.BehindTheApprovingFrame, true, TestName = "a payer-less sender covers the fee and the leading frame's value")]
    [TestCase(-1, true, SenderFramePosition.BehindTheApprovingFrame, true, TestName = "a payer-less sender falls one wei short of the leading frame's value")]
    [TestCase(-1, true, SenderFramePosition.BehindThePrologueDeployFrame, false, TestName = "a deploy frame opening the prefix moves no wei, so the value is still summed")]
    [TestCase(-1, true, SenderFramePosition.BehindTheExpiryAndDeployFrames, false, TestName = "an expiry frame ahead of that deploy frame does not lift the bound either")]
    [TestCase(-1, true, SenderFramePosition.BehindThePrologueDeployFrame, true, TestName = "a payer-less deploy-and-use layout is bounded too")]
    [TestCase(-1, false, SenderFramePosition.BehindAPostPrefixDefaultFrame, false, TestName = "a SENDER frame a post-prefix DEFAULT frame could fund is not summed")]
    public void Accept_SelfPayingSender_CountsTheValueOfALeadingSenderFrame(int balanceDelta, bool rejected, SenderFramePosition position, bool payerless)
    {
        const int frameValue = 4_000;
        TxFrame senderFrame = new(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressC, FrameTxTestFrames.PrefixFrameGas, (UInt256)frameValue, default);
        Transaction tx = FrameTxTestFrames.FrameTx(FramesFor(position, senderFrame));
        tx.DecodedMaxFeePerGas = UInt256.One;
        tx.Hash = TestItem.KeccakA;
        tx.PayerAddress = payerless ? null : TestItem.AddressA;

        // Priced off the fixture rather than a literal, so this pins the bound and not the gas schedule.
        Assert.That(FrameTxValidation.TryCalculateMaxCost(tx, Spec, out UInt256 maxCost), Is.True);
        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, maxCost + (UInt256)(frameValue + balanceDelta));

        PayerExposureCache cache = new();
        AcceptTxResult result = Accept(new TestReadOnlyStateProvider(), cache, tx, senderAccounts);

        UInt256 expectedPriced = position == SenderFramePosition.BehindAPostPrefixDefaultFrame ? maxCost : maxCost + (UInt256)frameValue;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.InsufficientFunds : AcceptTxResult.Accepted));
            // The payer-less leg records the price without reserving it; that record is what the bucket walk sums.
            Assert.That(tx.PayerExposure, rejected ? Is.Null : Is.EqualTo(expectedPriced),
                "an admitted leading SENDER frame is priced with its value alongside the fee");
            Assert.That(cache.GetReserved(TestItem.AddressA), Is.EqualTo(rejected || payerless ? UInt256.Zero : expectedPriced));
        }
    }

    private static TxFrame[] FramesFor(SenderFramePosition position, TxFrame senderFrame) => position switch
    {
        SenderFramePosition.BehindThePrologueDeployFrame => [FrameTxTestFrames.Deploy(), FrameTxTestFrames.SelfVerify(), senderFrame],
        SenderFramePosition.BehindTheExpiryAndDeployFrames => [FrameTxTestFrames.Expiry(), FrameTxTestFrames.Deploy(), FrameTxTestFrames.SelfVerify(), senderFrame],
        SenderFramePosition.BehindAPostPrefixDefaultFrame => [FrameTxTestFrames.SelfVerify(), FrameTxTestFrames.Deploy(), senderFrame],
        _ => [FrameTxTestFrames.SelfVerify(), senderFrame],
    };

    // The sponsor's reservation prices gas alone, so folding the sender's value into it would gate the wrong
    // account: the same frame must meet the same balance requirement whoever pays the fee.
    [TestCase(0, false, TestName = "a sponsored sender holds the leading frame's value")]
    [TestCase(-1, true, TestName = "a sponsored sender falls one wei short of it")]
    public void Accept_SponsoredFrameTx_HoldsTheSenderToItsOwnLeadingFrameValue(int balanceDelta, bool rejected)
    {
        const int frameValue = 4_000;
        TxFrame senderFrame = new(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressC, FrameTxTestFrames.PrefixFrameGas, (UInt256)frameValue, default);
        Transaction tx = FrameTxTestFrames.FrameTx(FrameTxTestFrames.OnlyVerify(), FrameTxTestFrames.Pay(Payer), senderFrame);
        tx.DecodedMaxFeePerGas = UInt256.One;
        tx.Hash = TestItem.KeccakA;
        tx.PayerAddress = Payer;

        // The sponsor is funded well past the fee, so only the sender's own balance can decide this.
        Assert.That(FrameTxValidation.TryCalculateMaxCost(tx, Spec, out UInt256 maxCost), Is.True);
        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)(frameValue + balanceDelta));

        PayerExposureCache cache = new();
        AcceptTxResult result = Accept(StateWithPayerBalance(long.MaxValue), cache, tx, senderAccounts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.InsufficientFunds : AcceptTxResult.Accepted));
            Assert.That(cache.GetReserved(Payer), Is.EqualTo(rejected ? UInt256.Zero : maxCost),
                "the sponsor still reserves the fee alone, the sender's value never being its liability");
        }
    }

    // Result equality is by id, so nothing else here would notice the split; a remote submitter never reads the
    // detail, and composing it on the path an unfunded flood walks is what the sibling filters guard against.
    [TestCase(false, TxHandlingOptions.None, false, TestName = "a remote self-paid rejection is message-free")]
    [TestCase(false, TxHandlingOptions.PersistentBroadcast, true, TestName = "a local self-paid rejection is detailed")]
    [TestCase(true, TxHandlingOptions.None, false, TestName = "a remote payer-less rejection is message-free")]
    [TestCase(true, TxHandlingOptions.PersistentBroadcast, true, TestName = "a local payer-less rejection is detailed")]
    public void Accept_UnderfundedSender_DetailsTheRejectionForALocalSubmissionOnly(bool payerless, TxHandlingOptions handlingOptions, bool detailed)
    {
        Transaction tx = FrameTxCostingExactly(TestCost, payer: TestItem.AddressA);
        if (payerless) tx.PayerAddress = null;
        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, (UInt256)(TestCost - 1));

        AcceptTxResult result = Accept(new TestReadOnlyStateProvider(), new PayerExposureCache(), tx, senderAccounts, handlingOptions: handlingOptions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.InsufficientFunds), "the rejection itself is the same either way");
            Assert.That(result.ToString(), detailed
                ? Does.Contain($"Account balance: {TestCost - 1}, pending cost: 0, transaction cost: {TestCost}")
                : Does.Not.Contain("Account balance"));
        }
    }

    [Test]
    public void ExposureCache_TryReserveWithinThenReleaseToZero()
    {
        PayerExposureCache cache = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryReserve(Payer, HashFor(1), 1000, balance: 1500, out _), Is.True);
            Assert.That(cache.TryReserve(Payer, HashFor(2), 500, balance: 1500, out _), Is.True, "reserved 1000 + 500 == balance is admitted");
            Assert.That(cache.TryReserve(Payer, HashFor(3), 1, balance: 1500, out _), Is.False, "one wei over the balance is rejected");
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)1500), "a rejected reservation adds nothing");
        }

        cache.Subtract(HashFor(1));
        Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)500));

        // Releases are keyed by transaction, so a repeated one releases nothing rather than another 1000.
        cache.Subtract(HashFor(1));
        Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)500));

        cache.Subtract(HashFor(2));
        Assert.That(cache.GetReserved(Payer), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void ExposureCache_ASecondReserveOnOneHashIsReleasedSeparately()
    {
        // Two submissions of one hash race the hash cache and one of them fails to insert and releases straight
        // away: that release must not take the reservation backing the copy the pool kept.
        PayerExposureCache cache = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryReserve(Payer, HashFor(1), 1000, balance: 10_000, out _), Is.True);
            Assert.That(cache.TryReserve(Payer, HashFor(1), 1000, balance: 10_000, out _), Is.True);
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)2000), "both reserves count against the payer");
        }

        cache.Subtract(HashFor(1));
        Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)1000), "the pooled copy is still backed");

        cache.Subtract(HashFor(1));
        Assert.That(cache.GetReserved(Payer), Is.EqualTo(UInt256.Zero));

        // Negative control: a release past the reserves stays a no-op rather than becoming free room.
        cache.Subtract(HashFor(1));
        Assert.That(cache.GetReserved(Payer), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void ExposureCache_ASecondReserveOnOneHashReleasesToItsOwnPayer()
    {
        // The payer is resolved per submission, so a repeat can name another one. Overwriting the entry would
        // strand the first payer's total with nothing left to release it.
        PayerExposureCache cache = new();
        cache.TryReserve(Payer, HashFor(1), 1000, balance: 10_000, out _);
        cache.TryReserve(TestItem.AddressC, HashFor(1), 700, balance: 10_000, out _);

        cache.Subtract(HashFor(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetReserved(TestItem.AddressC), Is.EqualTo(UInt256.Zero), "the newest reserve is released first");
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)1000));
        }

        cache.Subtract(HashFor(1));
        Assert.That(cache.GetReserved(Payer), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void MaxCost_CountsTheBlobTerm()
    {
        // A large max_fee_per_blob_gas must not smuggle unbounded exposure past a gas-only reservation.
        Transaction tx = FrameTxCostingExactly(TestCost);
        tx.BlobVersionedHashes = [new byte[32]];
        tx.MaxFeePerBlobGas = 1;

        Assert.That(FrameTxValidation.TryCalculateMaxCost(tx, Spec, out UInt256 maxCost), Is.True);
        Assert.That(maxCost, Is.EqualTo((UInt256)TestCost + Eip4844Constants.GasPerBlob));
    }

    [Test]
    public void ExposureCache_ClearReleasesEveryReservation()
    {
        // Pins the drain only: its paired gauge decrement is a shared static, so asserting that would race
        // the parallel fixtures.
        PayerExposureCache cache = new();
        cache.TryReserve(Payer, HashFor(1), 1000, balance: 1000, out _);
        cache.TryReserve(TestItem.AddressC, HashFor(2), 500, balance: 500, out _);

        cache.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetReserved(Payer), Is.EqualTo(UInt256.Zero));
            Assert.That(cache.GetReserved(TestItem.AddressC), Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void ExposureCache_RejectsAnAccumulationThatWouldOverflow()
    {
        // Overflow is checked before the balance compare: a wrapped total would silently re-open the gate.
        PayerExposureCache cache = new();
        Assert.That(cache.TryReserve(Payer, HashFor(1), UInt256.MaxValue, UInt256.MaxValue, out _), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryReserve(Payer, HashFor(2), 1, UInt256.MaxValue, out _), Is.False);
            Assert.That(cache.GetReserved(Payer), Is.EqualTo(UInt256.MaxValue));
        }
    }

    [Test]
    public void ExposureCache_ZeroCostReserveLeavesNoEntry()
    {
        // Subtract early-returns on zero, so a zero reservation would leave an entry nothing reclaims.
        PayerExposureCache cache = new();

        Assert.That(cache.TryReserve(Payer, HashFor(1), UInt256.Zero, balance: 1000, out _), Is.True);
        Assert.That(cache.GetReserved(Payer), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void ExposureCache_ConcurrentReservationsNeverExceedTheBalance()
    {
        // The reservation must be atomic: a check-then-act version admits more than the balance fits.
        const int fits = 8;
        PayerExposureCache cache = new();
        int accepted = 0;

        Parallel.For(0, 64, i =>
        {
            if (cache.TryReserve(Payer, HashFor(i), 1000, balance: fits * 1000, out UInt256 _)) Interlocked.Increment(ref accepted);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(fits));
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)(fits * 1000)));
        }
    }

    /// <summary>A distinct transaction hash per reservation: the ledger keys live reservations by hash.</summary>
    private static Hash256 HashFor(int seed)
    {
        byte[] bytes = new byte[Hash256.Size];
        BinaryPrimitives.WriteInt32BigEndian(bytes, seed);
        return new Hash256(bytes);
    }

    private static TestReadOnlyStateProvider StateWithPayerBalance(long wei)
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Payer, (UInt256)wei);
        return state;
    }

    /// <summary>A blob-carrying frame tx whose max cost is exactly <paramref name="cost"/>: the blob leg is
    /// priced at zero, so the gas leg alone decides it.</summary>
    private static Transaction BlobFrameTxCosting(int cost)
    {
        Transaction tx = FrameTxCostingExactly(cost);
        tx.BlobVersionedHashes = [new byte[32]];
        tx.MaxFeePerBlobGas = UInt256.Zero;
        return tx;
    }

    /// <summary>A blob-carrying frame tx sponsored through a <c>pay</c> frame, with no payer resolved.</summary>
    private static Transaction SponsoredFrameTx()
    {
        Transaction tx = BlobFrameTxCosting(TestCost);
        tx.Frames = [FrameTxTestFrames.OnlyVerify(FrameTxTestFrames.PrefixFrameGas), FrameTxTestFrames.Pay(TestItem.AddressD, FrameTxTestFrames.PrefixFrameGas)];
        tx.PayerAddress = null;
        return tx;
    }

    /// <summary>The same frame tx as <see cref="FrameTxCostingExactly"/>, carrying <paramref name="blobCount"/> blobs.</summary>
    private static Transaction BlobFrameTx(int blobCount, int maxFeePerBlobGas)
    {
        Transaction tx = FrameTxCostingExactly(TestCost);
        byte[][] hashes = new byte[blobCount][];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = new byte[32];
        }

        tx.BlobVersionedHashes = hashes;
        tx.MaxFeePerBlobGas = (UInt256)maxFeePerBlobGas;
        return tx;
    }

    /// <summary>A frame tx whose EIP-8141 <c>TXPARAM(0x06)</c> max cost is exactly <paramref name="cost"/> wei.</summary>
    private static Transaction FrameTxCostingExactly(int cost, Address? payer = null)
    {
        // At max_fee_per_gas == 1 the cost is the gas budget, so the frame's gas limit is the
        // requested cost less the spec-priced intrinsic component.
        Assert.That(FrameTxValidation.TryCalculateGasBudget(FrameTx(0), Spec, out ulong intrinsicGas, out _, out _), Is.True);
        Assert.That(intrinsicGas, Is.LessThan((ulong)cost), "the requested cost must leave room for the intrinsic term");
        return FrameTx((ulong)cost - intrinsicGas, payer);
    }

    private static Transaction FrameTx(ulong frameGasLimit, Address? payer = null) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, frameGasLimit, UInt256.Zero, default)],
        FrameSignatures = [],
        DecodedMaxFeePerGas = UInt256.One,
        PayerAddress = payer ?? Payer,
    };

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, PayerExposureCache cache, Transaction tx, IAccountStateProvider? senderAccounts = null, TxDistinctSortedPool? pending = null,
        TxHandlingOptions handlingOptions = TxHandlingOptions.None)
    {
        // The displaced tx sits in whichever pool matches its shape, so both are wired as TxPool does.
        (TxDistinctSortedPool standard, TxDistinctSortedPool blob) = tx.CarriesBlobs
            ? (Pool(blobs: false), pending ?? Pool(blobs: true))
            : (pending ?? Pool(blobs: false), Pool(blobs: true));
        // The filter takes no spec provider, so the spec below is the only one it can price against.
        FrameTxPayerExposureFilter filter = new(state, standard, blob, cache, LimboLogs.Instance.GetClassLogger<FrameTxPayerExposureFilterTests>());
        TxFilteringState filteringState = new(tx, senderAccounts ?? Substitute.For<IAccountStateProvider>(), Spec);
        return filter.Accept(tx, ref filteringState, handlingOptions);
    }
}
