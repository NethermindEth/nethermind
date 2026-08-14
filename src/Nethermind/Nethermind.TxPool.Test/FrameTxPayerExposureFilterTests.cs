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
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>
/// EIP-8141 per-payer exposure gate: a frame transaction is rejected once its resolved payer's
/// summed pending maximum cost would exceed the payer's balance, and accepted while it stays within.
/// </summary>
public class FrameTxPayerExposureFilterTests
{
    private static readonly Address Payer = TestItem.AddressB;

    // The bound is inclusive: a tx whose reserved + max_cost exactly equals the balance is admitted,
    // matching the spec's strict `available < tx.max_cost` rejection condition (ethereum/EIPs#12007).
    [TestCase(1000, 0, false, TestName = "single tx within balance")]
    [TestCase(999, 0, true, TestName = "single tx over balance")]
    [TestCase(1500, 1000, true, TestName = "summed exposure over balance")]
    [TestCase(2000, 1000, false, TestName = "summed exposure at inclusive boundary")]
    public void Accept_GatesOnPayerExposure(int balance, int reserved, bool rejected)
    {
        TestReadOnlyStateProvider state = StateWithPayerBalance(balance);
        PayerExposureCache cache = new();
        if (reserved > 0) cache.TryReserve(Payer, (UInt256)reserved, UInt256.MaxValue, out _);

        AcceptTxResult result = Accept(state, cache, FrameTxCostingExactly(1000));

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.FrameTxPayerExposureExceeded : AcceptTxResult.Accepted));
    }

    // Round-1 defect: pricing the bound on the gas leg alone let a frame tx name blob hashes at an
    // arbitrary max_fee_per_blob_gas and hold exposure the bound never counted. Pinned by magnitude, so
    // dropping either factor of GasPerBlob * blob count * MaxFeePerBlobGas moves the boundary and fails.
    [TestCase(1, 3, TestName = "one blob")]
    [TestCase(2, 5, TestName = "two blobs")]
    [TestCase(6, 1_000_000, TestName = "six blobs at a realistic blob fee")]
    public void Accept_BlobCarryingFrameTx_ReservesTheBlobTermToo(int blobCount, int maxFeePerBlobGas)
    {
        // Widened: the product exceeds int at six blobs and a realistic blob fee.
        long blobTerm = (long)Eip4844Constants.GasPerBlob * blobCount * maxFeePerBlobGas;
        PayerExposureCache cache = new();

        AcceptTxResult atBound = Accept(StateWithPayerBalance(1000 + blobTerm), cache, BlobFrameTx(blobCount, maxFeePerBlobGas));
        AcceptTxResult oneWeiShort = Accept(StateWithPayerBalance(1000 + blobTerm - 1), new PayerExposureCache(), BlobFrameTx(blobCount, maxFeePerBlobGas));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(atBound, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)(1000 + blobTerm)), "the gas leg and the whole blob term are reserved");
            Assert.That(oneWeiShort, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded));
        }
    }

    // The gate runs before AddCore resolves the replacement, so the displaced tx is still reserved. The
    // bound is on the pending set the pool would hold, and only a tx this one displaces may be discounted
    // from it — same sender, same nonce, and the same payer, or some other payer is the one being freed.
    [TestCase(0ul, false, false, TestName = "a fee bump discounts the tx it displaces")]
    [TestCase(1ul, false, true, TestName = "a later nonce joins the pending set instead")]
    [TestCase(0ul, true, true, TestName = "an incumbent paid by another payer frees that one")]
    public void Accept_DiscountsOnlyTheReservationItDisplaces(ulong bumpNonce, bool incumbentPaidByAnother, bool rejected)
    {
        Transaction incumbent = FrameTxCostingExactly(600, payer: incumbentPaidByAnother ? TestItem.AddressC : null);
        incumbent.Hash = TestItem.KeccakA;
        Transaction bump = FrameTxCostingExactly(700);
        bump.Nonce = bumpNonce;
        bump.Hash = TestItem.KeccakB;

        // 600 + 700 exceeds the balance, so only discounting the displaced 600 can admit the bump. In the
        // third case the 600 is synthetic: it stands in for another pending tx of Payer's, since a
        // reservation with nothing pending behind it is not a state admission can reach.
        PayerExposureCache cache = new();
        cache.TryReserve(Payer, 600, balance: 1200, out _);

        AcceptTxResult result = Accept(StateWithPayerBalance(1200), cache, bump, pending: Pool(blobs: false, incumbent));

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.FrameTxPayerExposureExceeded : AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_ReservesOnAdmission_SoASecondTxFromOnePayerSeesIt()
    {
        TestReadOnlyStateProvider state = StateWithPayerBalance(1500);
        PayerExposureCache cache = new();

        AcceptTxResult first = Accept(state, cache, FrameTxCostingExactly(1000));
        AcceptTxResult second = Accept(state, cache, FrameTxCostingExactly(1000));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(second, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded));
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)1000), "only the admitted tx is reserved");
        }
    }

    [Test]
    public void Accept_UnresolvedFramePayer_PassesThrough()
    {
        // FrameTxPayerFilter left the payer null (RequiresSimulation / NoPayer): not gated here.
        Transaction tx = FrameTxCostingExactly(1000);
        tx.PayerAddress = null;

        AcceptTxResult result = Accept(StateWithPayerBalance(0), new PayerExposureCache(), tx);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_NonFrameTx_PassesThrough()
    {
        Transaction tx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;

        AcceptTxResult result = Accept(StateWithPayerBalance(0), new PayerExposureCache(), tx);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [TestCase(1000, false, TestName = "self-paying sender within its balance")]
    [TestCase(999, true, TestName = "self-paying sender over its balance")]
    public void Accept_SelfPayingSender_GatesOnTheAccountTheSiblingBalanceFiltersUsed(int balance, bool rejected)
    {
        // Native resolution only ever yields payer == sender today, so this is the branch every real
        // admission takes: it must read the cached sender account, not the state provider.
        Transaction tx = FrameTxCostingExactly(1000, payer: TestItem.AddressA);
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

    /// <summary>The same frame tx as <see cref="FrameTxCostingExactly"/>, carrying <paramref name="blobCount"/> blobs.</summary>
    private static Transaction BlobFrameTx(int blobCount, int maxFeePerBlobGas)
    {
        Transaction tx = FrameTxCostingExactly(1000);
        byte[][] hashes = new byte[blobCount][];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = new byte[32];
        }

        tx.BlobVersionedHashes = hashes;
        tx.MaxFeePerBlobGas = (UInt256)maxFeePerBlobGas;
        return tx;
    }

    /// <summary>A frame tx whose max cost (gas only, <c>MaxFeePerGas * GasLimit</c>) is exactly <paramref name="cost"/>.</summary>
    private static Transaction FrameTxCostingExactly(int cost, Address? payer = null) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        GasLimit = 1,
        DecodedMaxFeePerGas = (UInt256)cost,
        PayerAddress = payer ?? Payer,
    };

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, PayerExposureCache cache, Transaction tx, IAccountStateProvider? senderAccounts = null, TxDistinctSortedPool? pending = null)
    {
        // The displaced tx sits in whichever pool matches its shape, so both are wired as TxPool does.
        (TxDistinctSortedPool standard, TxDistinctSortedPool blob) = tx.CarriesBlobs
            ? (Pool(blobs: false), pending ?? Pool(blobs: true))
            : (pending ?? Pool(blobs: false), Pool(blobs: true));
        FrameTxPayerExposureFilter filter = new(state, standard, blob, cache, LimboLogs.Instance.GetClassLogger<FrameTxPayerExposureFilterTests>());
        TxFilteringState filteringState = new(tx, senderAccounts ?? Substitute.For<IAccountStateProvider>());
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
