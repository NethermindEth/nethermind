// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

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

    [Test]
    public void Accept_SingleFrameTx_WithinBalance_Accepted()
    {
        TestReadOnlyStateProvider state = StateWithPayerBalance(1000);
        PayerExposureCache cache = new();

        AcceptTxResult result = Accept(state, cache, FrameTxCostingExactly(1000));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_SingleFrameTx_ExceedingBalance_Rejected()
    {
        TestReadOnlyStateProvider state = StateWithPayerBalance(999);
        PayerExposureCache cache = new();

        AcceptTxResult result = Accept(state, cache, FrameTxCostingExactly(1000));

        Assert.That(result, Is.EqualTo(AcceptTxResult.PayerExposureExceeded));
    }

    [Test]
    public void Accept_SecondFrameTx_SummedExposureExceedsBalance_Rejected()
    {
        // Each tx costs 1000 and the payer holds 1500: individually affordable, jointly not.
        TestReadOnlyStateProvider state = StateWithPayerBalance(1500);
        PayerExposureCache cache = new();

        AcceptTxResult first = Accept(state, cache, FrameTxCostingExactly(1000));
        // The pool would account the accepted tx on insertion; simulate that reservation.
        cache.Add(Payer, 1000);
        AcceptTxResult second = Accept(state, cache, FrameTxCostingExactly(1000));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(second, Is.EqualTo(AcceptTxResult.PayerExposureExceeded));
        }
    }

    [Test]
    public void Accept_SecondFrameTx_SummedExposureWithinBalance_Accepted()
    {
        TestReadOnlyStateProvider state = StateWithPayerBalance(2000);
        PayerExposureCache cache = new();

        AcceptTxResult first = Accept(state, cache, FrameTxCostingExactly(1000));
        cache.Add(Payer, 1000);
        AcceptTxResult second = Accept(state, cache, FrameTxCostingExactly(1000));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(second, Is.EqualTo(AcceptTxResult.Accepted));
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
    public void ExposureCache_AddThenSubtract_ReturnsToZero()
    {
        PayerExposureCache cache = new();
        cache.Add(Payer, 1000);
        cache.Add(Payer, 500);
        cache.Subtract(Payer, 1000);

        Assert.That(cache.GetReserved(Payer), Is.EqualTo((UInt256)500));

        cache.Subtract(Payer, 500);
        Assert.That(cache.GetReserved(Payer), Is.EqualTo(UInt256.Zero));
    }

    private static TestReadOnlyStateProvider StateWithPayerBalance(int wei)
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Payer, (UInt256)wei);
        return state;
    }

    /// <summary>A frame tx whose max cost (<c>MaxFeePerGas * GasLimit + Value</c>) is exactly <paramref name="cost"/>.</summary>
    private static Transaction FrameTxCostingExactly(int cost) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        GasLimit = 100,
        DecodedMaxFeePerGas = (UInt256)(cost / 100),
        PayerAddress = Payer,
    };

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, PayerExposureCache cache, Transaction tx)
    {
        FrameTxPayerExposureFilter filter = new(state, cache, LimboLogs.Instance.GetClassLogger<FrameTxPayerExposureFilterTests>());
        TxFilteringState filteringState = new(tx, Substitute.For<IAccountStateProvider>());
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }
}
