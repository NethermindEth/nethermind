// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
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

    [Test]
    public void Accept_ReservesOnAdmission_SoASecondTxFromOnePayerSeesIt()
    {
        TestReadOnlyStateProvider state = StateWithPayerBalance(TestCost + TestCost / 2);
        PayerExposureCache cache = new();

        AcceptTxResult first = Accept(state, cache, FrameTxCostingExactly(TestCost));
        AcceptTxResult second = Accept(state, cache, FrameTxCostingExactly(TestCost));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(second, Is.EqualTo(AcceptTxResult.FrameTxPayerExposureExceeded));
            Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)TestCost), "only the admitted tx is reserved");
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

    [TestCase(1000, false, TestName = "self-paying sender within its balance")]
    [TestCase(999, true, TestName = "self-paying sender over its balance")]
    public void Accept_SelfPayingSender_GatesOnTheAccountTheSiblingBalanceFiltersUsed(int balance, bool rejected)
    {
        // Native resolution only ever yields payer == sender today, so this is the branch every real
        // admission takes: it must read the cached sender account, not the state provider.
        Transaction tx = FrameTxCostingExactly(TestCost, payer: TestItem.AddressA);
        TestReadOnlyStateProvider senderAccounts = new();
        senderAccounts.CreateAccount(TestItem.AddressA, rejected ? (UInt256)(TestCost - 1) : (UInt256)TestCost);

        // The state provider is left empty: reading it instead would see a zero balance and always reject.
        AcceptTxResult result = Accept(new TestReadOnlyStateProvider(), new PayerExposureCache(), tx, senderAccounts);

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.FrameTxPayerExposureExceeded : AcceptTxResult.Accepted));
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

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, PayerExposureCache cache, Transaction tx, IAccountStateProvider? senderAccounts = null)
    {
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        specProvider.GetCurrentHeadSpec().Returns(Spec);
        FrameTxPayerExposureFilter filter = new(specProvider, state, cache, LimboLogs.Instance.GetClassLogger<FrameTxPayerExposureFilterTests>());
        TxFilteringState filteringState = new(tx, senderAccounts ?? Substitute.For<IAccountStateProvider>());
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }
}
