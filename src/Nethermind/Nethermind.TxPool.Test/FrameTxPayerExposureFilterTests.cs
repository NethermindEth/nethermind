// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
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

    private static TestReadOnlyStateProvider StateWithPayerBalance(int wei)
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Payer, (UInt256)wei);
        return state;
    }

    /// <summary>A frame tx whose max cost (gas only, <c>MaxFeePerGas * GasLimit</c>) is exactly <paramref name="cost"/>.</summary>
    private static Transaction FrameTxCostingExactly(int cost) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        GasLimit = 1,
        DecodedMaxFeePerGas = (UInt256)cost,
        PayerAddress = Payer,
    };

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, PayerExposureCache cache, Transaction tx)
    {
        FrameTxPayerExposureFilter filter = new(state, cache, LimboLogs.Instance.GetClassLogger<FrameTxPayerExposureFilterTests>());
        TxFilteringState filteringState = new(tx, Substitute.For<IAccountStateProvider>());
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }
}
