// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor.Test;

[TestFixture]
internal class RefundMachineTests
{
    private static readonly RefundConstants Constants = new(16_777_216, 183_600, 25_000, 12_500, 2, 5);
    private static readonly GasState ZeroGas = new(0, 0, 0, 0, 0);

    private static RefundObservations Baseline => new(
        RefundEntry.OrdinaryRefund, 1_000_000, 3, 1, 0, false, false, true, true, true,
        false, false, 0, 0, 24_000, 0,
        new(600_000, 100_000, 200_000, 90_000, 10_000),
        new(21_000, 100_000, 100_000, 0, 0),
        ZeroGas, 100_000, false);

    [Test]
    public void Normal_tail_derives_all_settlement_inputs()
    {
        RefundObservations input = Baseline with { RefundCounter = 100, DestroyCount = 2, CodeInsertRefundCount = 4 };
        RefundResult result = RefundMachine.Evaluate(input, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Settlement, Is.EqualTo(new SettlementInput(1_000_000, 300_000, 100, 2, 24_000, 0, 0, 200_000, 5, false, false, true, true)));
            Assert.That(result.Consumed, Is.EqualTo(new ConsumedGas(251_900, 251_900, 100_000, 200_000, 300_000, 48_100)));
            Assert.That(result.CallerGas, Is.EqualTo(input.IncomingGas));
            Assert.That(result.WorkingGas, Is.EqualTo(input.IncomingGas));
            Assert.That(result.SenderCredit, Is.EqualTo(new BigInteger(2_244_300)));
        }
    }

    [Test]
    public void Create_revert_refills_reservoir_without_marking_or_restoring_spill()
    {
        RefundObservations input = Baseline with
        {
            ShouldRevert = true, IsContractCreation = true, TopLevelCreateStateGasCharged = true,
            IncomingGas = new(400_000, 100_000, 300_000, 190_000, 20_000),
        };
        RefundResult result = RefundMachine.Evaluate(input, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WorkingGas, Is.EqualTo(new GasState(400_000, 283_600, 116_400, 190_000, 20_000)));
            Assert.That(result.CallerGas, Is.EqualTo(input.IncomingGas));
            Assert.That(result.Settlement!.PreRefundGas, Is.EqualTo(316_400));
            Assert.That(result.Settlement.StateGasUsed, Is.EqualTo(116_400));
            Assert.That(result.Consumed.GasRefund, Is.Zero);
        }
    }

    [Test]
    public void Create_refill_is_guarded_by_each_observation(
        [Values] bool enabled, [Values] bool revert, [Values] bool charged, [Values] bool creation)
    {
        RefundObservations input = Baseline with
        {
            IsEip8037Enabled = enabled, ShouldRevert = revert,
            TopLevelCreateStateGasCharged = charged, IsContractCreation = creation,
        };
        RefundResult result = RefundMachine.Evaluate(input, Constants);
        Assert.That(result.WorkingGas != input.IncomingGas, Is.EqualTo(enabled && revert && charged && creation));
    }

    [Test]
    public void Halt_preserves_refunded_spill_after_reset_and_burns_refilled_execution()
    {
        RefundResult result = RefundMachine.Evaluate(Baseline with { IsError = true }, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WorkingGas, Is.EqualTo(new GasState(0, 100_000, 0, 0, 90_000)));
            Assert.That(result.HaltStateFloor, Is.Zero);
            Assert.That(result.Settlement, Is.Null);
            Assert.That(result.Consumed, Is.EqualTo(new ConsumedGas(900_000, 900_000, 900_000, 0, 900_000, 0)));
            Assert.That(result.WorkingGas.StateGasSpillRefunded, Is.GreaterThan(result.WorkingGas.StateGasSpill));
        }
    }

    [Test]
    public void Each_halt_route_retains_its_copy_or_ref_footprint([Values] RefundEntry entry)
    {
        RefundObservations input = Baseline with { Entry = entry, IsError = entry == RefundEntry.OrdinaryRefund };
        RefundResult result = RefundMachine.Evaluate(input, Constants);
        bool copy = entry is RefundEntry.OrdinaryRefund or RefundEntry.ContractCollision;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.CallerGas, Is.EqualTo(copy ? input.IncomingGas : result.WorkingGas));
            Assert.That(result.Settlement, Is.Null);
            Assert.That(result.WorkingGas.Value, Is.Zero);
        }
    }

    [Test]
    public void Failed_deposit_selects_halt_even_when_substate_is_not_error()
    {
        RefundResult result = RefundMachine.Evaluate(Baseline with { Entry = RefundEntry.FailedDeposit, IsError = false }, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Settlement, Is.Null);
            Assert.That(result.CallerGas.Value, Is.Zero);
            Assert.That(result.CallerGas.StateGasSpillRefunded, Is.EqualTo(90_000));
        }
    }

    [Test]
    public void Legacy_collision_and_deposit_return_full_gas_without_payment(
        [Values(RefundEntry.ContractCollision, RefundEntry.FailedDeposit)] RefundEntry entry)
    {
        RefundObservations input = Baseline with { Entry = entry, IsEip8037Enabled = false };
        RefundResult result = RefundMachine.Evaluate(input, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Consumed, Is.EqualTo(new ConsumedGas(1_000_000, 1_000_000, 0, 0, 1_000_000, 0)));
            Assert.That(result.CallerGas, Is.EqualTo(input.IncomingGas));
            Assert.That(result.PayRefundCalled, Is.False);
            Assert.That(result.SenderCredit, Is.Null);
        }
    }

    [TestCase(0UL, 0L, 100UL, 100UL)]
    [TestCase(80UL, 30L, 100UL, 100UL)]
    [TestCase(80UL, -30L, 100UL, 50UL)]
    [TestCase(0UL, -1L, ulong.MaxValue, ulong.MaxValue)]
    [TestCase(100UL, 0L, 100UL, 0UL)]
    public void Pre_refund_uses_wide_signed_difference_and_full_limit_fallback(ulong execution, long reservoir, ulong limit, ulong expected) =>
        Assert.That(RefundMachine.PreRefundGas(new(execution, reservoir, 0, 0, 0), limit), Is.EqualTo(expected));

    [Test]
    public void Payment_predicate_requires_price_and_validation_or_a_fee_cap(
        [Values] bool price, [Values] bool skip, [Values] bool fee, [Values] bool priority)
    {
        RefundObservations input = Baseline with
        {
            GasPrice = price ? 1 : 0, SkipValidation = skip, MaxFeePerGas = fee ? 1 : 0, MaxPriorityFeePerGas = priority ? 1 : 0,
        };
        Assert.That(RefundMachine.ShouldRefund(input), Is.EqualTo(price && (!skip || fee || priority)));
    }

    [Test]
    public void Normal_zero_amount_calls_PayRefund_but_has_no_sender_credit()
    {
        RefundResult result = RefundMachine.Evaluate(Baseline with { IncomingGas = ZeroGas }, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PayRefundCalled, Is.True);
            Assert.That(result.PaymentAmount, Is.EqualTo(BigInteger.Zero));
            Assert.That(result.SenderCredit, Is.Null);
        }
    }

    [Test]
    public void Halt_zero_amount_does_not_call_PayRefund()
    {
        RefundResult result = RefundMachine.Evaluate(Baseline with { IsError = true, PostIntrinsicStateReservoir = 0 }, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PayRefundCalled, Is.False);
            Assert.That(result.SenderCredit, Is.Null);
        }
    }

    [Test]
    public void Reset_keeps_execution_and_refunded_spill_but_clear_changes_only_execution()
    {
        GasState initial = new(80, 20, 40, 60, 30);
        GasState reset = RefundMachine.ResetForHalt(initial, 10, 5);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reset, Is.EqualTo(new GasState(80, 10, 5, 0, 30)));
            Assert.That(RefundMachine.ClearExecutionGas(reset), Is.EqualTo(new GasState(0, 10, 5, 0, 30)));
        }
    }

    [TestCase(0UL, 0UL)]
    [TestCase(1UL, 12_500UL)]
    [TestCase(2UL, 25_000UL)]
    public void Legacy_code_refund_is_derived_from_the_count(ulong count, ulong expected)
    {
        RefundResult result = RefundMachine.Evaluate(Baseline with { IsEip8037Enabled = false, CodeInsertRefundCount = count }, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Settlement!.CodeInsertExecutionRefund, Is.EqualTo(expected));
            Assert.That(result.Settlement.StateGasUsed, Is.Zero);
        }
    }

    [Test]
    public void Negative_refund_increases_operation_gas_instead_of_becoming_zero()
    {
        RefundResult result = RefundMachine.Evaluate(Baseline with { RefundCounter = -100 }, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Consumed.OperationGas, Is.EqualTo(300_100));
            Assert.That(result.Consumed.GasRefund, Is.Zero);
        }
    }

    [Test]
    public void Floor_keeps_normal_operation_and_maximum_projections_distinct_from_halt_projection()
    {
        RefundObservations input = Baseline with { FloorGas = new(500_000, 0, 0, 0, 0), RefundCounter = 100 };
        RefundResult result = RefundMachine.Evaluate(input, Constants);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Consumed.SpentGas, Is.EqualTo(500_000));
            Assert.That(result.Consumed.OperationGas, Is.EqualTo(299_900));
            Assert.That(result.Consumed.MaxUsedGas, Is.EqualTo(500_000));
        }
    }

    [Test]
    public void Payment_retains_unsigned_subtraction_and_UInt256_wrap()
    {
        RefundResult result = RefundMachine.Evaluate(Baseline with
        {
            TransactionGasLimit = 1, IncomingGas = ZeroGas, FloorGas = new(2, 0, 0, 0, 0), GasPrice = (BigInteger.One << 256) - 1,
        }, Constants);
        Assert.That(result.PaymentAmount, Is.EqualTo((BigInteger.One << 256) - ulong.MaxValue));
    }

    [Test]
    public void Raw_UInt256_out_of_range_is_not_admitted([Values(-1, 256)] int marker)
    {
        BigInteger price = marker < 0 ? -1 : BigInteger.One << marker;
        Assert.That(() => RefundMachine.Evaluate(Baseline with { GasPrice = price }, Constants), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
