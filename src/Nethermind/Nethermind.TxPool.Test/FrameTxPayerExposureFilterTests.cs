// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>EIP-8141 per-payer exposure gate: a frame tx is rejected once its payer's summed pending max cost
/// would exceed the payer's balance.</summary>
public class FrameTxPayerExposureFilterTests
{
    private static readonly Address Payer = TestItem.AddressB;
    private static readonly IReleaseSpec Spec = Eip8141Prototype.Instance;
    private const int TestCost = 100_000;

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
        if (reserved > 0) cache.TryReserve(Payer, (UInt256)reserved, UInt256.MaxValue, out _);

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
        // Case three's reservation is synthetic: admission cannot leave Payer reserved with nothing pending.
        PayerExposureCache cache = new();
        cache.TryReserve(Payer, incumbentCost, balance: balance, out _);

        AcceptTxResult result = Accept(StateWithPayerBalance(balance), cache, bump, pending: Pool(blobs: false, incumbent));

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.FrameTxPayerExposureExceeded : AcceptTxResult.Accepted));
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
        cache.TryReserve(Payer, incumbentCost, balance: balance, out _);

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

    [Test]
    public void Accept_UnresolvedFramePayer_PassesThrough()
    {
        // FrameTxPayerFilter left the payer null (RequiresSimulation / NoPayer): not gated here.
        Transaction tx = FrameTxCostingExactly(TestCost);
        tx.PayerAddress = null;

        AcceptTxResult result = Accept(StateWithPayerBalance(0), new PayerExposureCache(), tx);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
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

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.FrameTxPayerExposureExceeded : AcceptTxResult.Accepted));
    }

    [Test]
    public void ExposureCache_TryReserveWithinThenReleaseToZero()
    {
        PayerExposureCache cache = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryReserve(Payer, 1000, balance: 1500, out _), Is.True);
            Assert.That(cache.TryReserve(Payer, 500, balance: 1500, out _), Is.True, "reserved 1000 + 500 == balance is admitted");
            Assert.That(cache.TryReserve(Payer, 1, balance: 1500, out _), Is.False, "one wei over the balance is rejected");
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)1500), "a rejected reservation adds nothing");
        }

        cache.Subtract(Payer, 1000);
        Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)500));

        // Over-release clamps at zero rather than wrapping, so the gate can never be disabled.
        cache.Subtract(Payer, 1000);
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
        cache.TryReserve(Payer, 1000, balance: 1000, out _);
        cache.TryReserve(TestItem.AddressC, 500, balance: 500, out _);

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
        Assert.That(cache.TryReserve(Payer, UInt256.MaxValue, UInt256.MaxValue, out _), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryReserve(Payer, 1, UInt256.MaxValue, out _), Is.False);
            Assert.That(cache.GetReserved(Payer), Is.EqualTo(UInt256.MaxValue));
        }
    }

    [Test]
    public void ExposureCache_ZeroCostReserveLeavesNoEntry()
    {
        // Subtract early-returns on zero, so a zero reservation would leave an entry nothing reclaims.
        PayerExposureCache cache = new();

        Assert.That(cache.TryReserve(Payer, UInt256.Zero, balance: 1000, out _), Is.True);
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
            if (cache.TryReserve(Payer, 1000, balance: fits * 1000, out UInt256 _)) Interlocked.Increment(ref accepted);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.EqualTo(fits));
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)(fits * 1000)));
        }
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

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, PayerExposureCache cache, Transaction tx, IAccountStateProvider? senderAccounts = null, TxDistinctSortedPool? pending = null)
    {
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        specProvider.GetCurrentHeadSpec().Returns(Spec);

        // The displaced tx sits in whichever pool matches its shape, so both are wired as TxPool does.
        (TxDistinctSortedPool standard, TxDistinctSortedPool blob) = tx.CarriesBlobs
            ? (Pool(blobs: false), pending ?? Pool(blobs: true))
            : (pending ?? Pool(blobs: false), Pool(blobs: true));
        FrameTxPayerExposureFilter filter = new(specProvider, state, standard, blob, cache, LimboLogs.Instance.GetClassLogger<FrameTxPayerExposureFilterTests>());
        TxFilteringState filteringState = new(tx, senderAccounts ?? Substitute.For<IAccountStateProvider>(), Spec);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    /// <summary>The real pool type for the shape, so the visitor's ascending-nonce exit is exercised as wired.</summary>
    private static TxDistinctSortedPool Pool(bool blobs, params Transaction[] pending)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new ReleaseSpec { IsEip1559Enabled = false });
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);

        IComparer<Transaction> comparer = new TransactionComparerProvider(specProvider, blockTree).GetDefaultComparer();
        TxDistinctSortedPool pool = blobs
            ? new BlobTxDistinctSortedPool(pending.Length + 1, comparer, LimboLogs.Instance)
            : new TxDistinctSortedPool(pending.Length + 1, comparer, LimboLogs.Instance);
        foreach (Transaction tx in pending)
        {
            pool.TryInsert(tx.Hash!, tx);
        }

        return pool;
    }
}
