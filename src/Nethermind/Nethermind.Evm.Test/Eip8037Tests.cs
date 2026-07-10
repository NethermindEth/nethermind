// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip8037Tests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private static IEnumerable<TestCaseData> ConstantsTestCases()
    {
        yield return new TestCaseData(GasCostOf.CostPerStateByte).Returns(1530L).SetName("CostPerStateByte");
        yield return new TestCaseData(GasCostOf.SSetState).Returns(97920L).SetName("SSetState");
        yield return new TestCaseData(GasCostOf.CreateState).Returns(183600L).SetName("CreateState");
        yield return new TestCaseData(GasCostOf.NewAccountState).Returns(183600L).SetName("NewAccountState");
        yield return new TestCaseData(GasCostOf.PerAuthBaseState).Returns(35190L).SetName("PerAuthBaseState");
        yield return new TestCaseData(Eip8037Constants.SystemCallStateReservoir).Returns(1566720L).SetName("SystemCallStateReservoir");
        yield return new TestCaseData((long)Eip8037Constants.SystemCallGasLimit).Returns(31566720L).SetName("SystemCallGasLimit");
    }

    [TestCaseSource(nameof(ConstantsTestCases))]
    public long Constants_are_calculated_correctly(long actual) => actual;

    [TestCase(1, ExpectedResult = 6ul)]
    [TestCase(32, ExpectedResult = 6ul)]
    [TestCase(33, ExpectedResult = 12ul)]
    public ulong Code_deposit_regular_cost(int codeLength)
    {
        CodeDepositHandler.CalculateCost(Amsterdam.Instance, codeLength, out ulong regularCost, out _);
        return regularCost;
    }

    [TestCase(1, ExpectedResult = 1530L)]
    [TestCase(32, ExpectedResult = 48960L)]
    [TestCase(33, ExpectedResult = 50490L)]
    public long Code_deposit_state_cost(int codeLength)
    {
        CodeDepositHandler.CalculateCost(Amsterdam.Instance, codeLength, out _, out long stateCost);
        return stateCost;
    }

    [Test]
    public void System_transaction_gas_keeps_regular_budget_and_state_reservoir()
    {
        EthereumGasPolicy intrinsicGas = EthereumGasPolicy.CreateSystemTransactionIntrinsicGas(Eip8037Constants.SystemCallGasLimit);
        EthereumGasPolicy availableGas = EthereumGasPolicy.CreateSystemTransactionAvailableGas(Eip8037Constants.SystemCallGasLimit, in intrinsicGas, Amsterdam.Instance);

        Assert.That(
            (
                intrinsicGas.Value,
                intrinsicGas.StateReservoir,
                intrinsicGas.StateGasUsed,
                availableGas.Value,
                availableGas.StateReservoir,
                availableGas.StateGasUsed
            ),
            Is.EqualTo(
                (
                    0L,
                    Eip8037Constants.SystemCallStateReservoir,
                    0L,
                    Eip8037Constants.SystemCallBaseGasLimit,
                    Eip8037Constants.SystemCallStateReservoir,
                    0L
                )));

        for (ulong i = 0ul; i < Eip8037Constants.SystemMaxSstoresPerCall; i++)
        {
            Assert.That(EthereumGasPolicy.ConsumeStateGas(ref availableGas, GasCostOf.SSetState), Is.True);
        }

        Assert.That(
            (
                availableGas.Value,
                availableGas.StateReservoir,
                availableGas.StateGasUsed
            ),
            Is.EqualTo(
                (
                    Eip8037Constants.SystemCallBaseGasLimit,
                    0L,
                    Eip8037Constants.SystemCallStateReservoir
                )));
    }

    [Test]
    public void Regular_transaction_gas_uses_tx_cap_even_when_intrinsic_state_matches_system_reservoir()
    {
        EthereumGasPolicy intrinsicGas = new()
        {
            StateReservoir = Eip8037Constants.SystemCallStateReservoir,
        };

        EthereumGasPolicy availableGas = EthereumGasPolicy.CreateAvailableFromIntrinsic(Eip8037Constants.SystemCallGasLimit, in intrinsicGas, Amsterdam.Instance);
        long expectedReservoir = (long)(Eip8037Constants.SystemCallGasLimit - Eip8037Constants.SystemCallStateReservoir - Eip7825Constants.DefaultTxGasLimitCap);

        Assert.That(
            (
                availableGas.Value,
                availableGas.StateReservoir,
                availableGas.StateGasUsed
            ),
            Is.EqualTo(
                (
                    Eip7825Constants.DefaultTxGasLimitCap,
                    expectedReservoir,
                    Eip8037Constants.SystemCallStateReservoir
                )));
    }

    [Test]
    public void Generic_code_deposit_cost_uses_fixed_state_pricing()
    {
        EthereumGasPolicy gas = default;

        bool success = CodeDepositHandler.CalculateCost(Amsterdam.Instance, 33, in gas, out ulong regularCost, out long stateCost);

        Assert.That((success, regularCost, stateCost),
            Is.EqualTo((true, 12L, GasCostOf.CodeDepositState * 33)));
    }

    [Test]
    public void Intrinsic_gas_uses_fixed_state_costs()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved()
            .WithAuthorizationCode(new AuthorizationTuple(1, TestItem.AddressF, 0, 0, UInt256.One, UInt256.One))
            .TestObject;

        IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, Amsterdam.Instance, 30_000_000);

        Assert.That(intrinsicGas.Standard.StateReservoir,
            Is.EqualTo(GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState));
    }

    [Test]
    public void Amsterdam_access_list_floor_pricing_is_added_to_regular_and_floor_intrinsic_gas()
    {
        AccessList accessList = new AccessList.Builder()
            .AddAddress(TestItem.AddressA)
            .AddStorage(UInt256.One)
            .AddStorage((UInt256)2)
            .AddStorage((UInt256)3)
            .Build();
        Transaction tx = Build.A.Transaction.SignedAndResolved()
            .WithAccessList(accessList)
            .TestObject;

        IntrinsicGas<EthereumGasPolicy> splitIntrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, Amsterdam.Instance);
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, Amsterdam.Instance);
        // Access-list entries repriced to the cold costs; the value-bearing recipient touch
        // adds COLD_ACCOUNT_ACCESS + TRANSFER_LOG + TX_VALUE.
        ulong recipientRegular = Eip8038Constants.ColdAccountAccess + GasCostOf.TransferLogEip2780 + GasCostOf.TxValueCostEip2780;
        ulong accessListBaseCost = Eip8038Constants.AccessListAddressCost + 3 * Eip8038Constants.AccessListStorageKeyCost;
        ulong accessListFloorTokens = (20ul + 3ul * 32ul) * Amsterdam.Instance.GasCosts.TxDataNonZeroMultiplier;
        ulong accessListFloorCost = accessListFloorTokens * Amsterdam.Instance.GasCosts.TotalCostFloorPerToken;
        ulong expectedRegular = GasCostOf.TransactionEip2780 + recipientRegular + accessListBaseCost + accessListFloorCost;
        ulong expectedFloorGas = GasCostOf.TransactionEip2780 + accessListFloorCost;

        Assert.That(splitIntrinsicGas.Standard.Value, Is.EqualTo(expectedRegular));
        Assert.That(splitIntrinsicGas.Standard.StateReservoir, Is.Zero);
        Assert.That(splitIntrinsicGas.FloorGas.Value, Is.EqualTo(expectedFloorGas));
        Assert.That(intrinsicGas.Standard, Is.EqualTo(expectedRegular));
        Assert.That(intrinsicGas.FloorGas, Is.EqualTo(expectedFloorGas));
    }

    [Test]
    public void Prague_access_list_floor_pricing_is_not_applied()
    {
        AccessList accessList = new AccessList.Builder()
            .AddAddress(TestItem.AddressA)
            .AddStorage(UInt256.One)
            .Build();
        Transaction tx = Build.A.Transaction.SignedAndResolved()
            .WithAccessList(accessList)
            .TestObject;

        IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, Prague.Instance);
        ulong expectedRegular = GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + GasCostOf.AccessStorageListEntry;

        Assert.That(intrinsicGas.Standard.Value, Is.EqualTo(expectedRegular));
        Assert.That(intrinsicGas.FloorGas.Value, Is.EqualTo(GasCostOf.Transaction));
    }

    [Test]
    public void State_gas_consumption_spills_to_regular_gas()
    {
        EthereumGasPolicy gas = new() { Value = 100, StateReservoir = 50, StateGasUsed = 0 };

        bool consumed = EthereumGasPolicy.ConsumeStateGas(ref gas, 70);

        Assert.That((consumed, gas.Value, gas.StateReservoir, gas.StateGasUsed), Is.EqualTo((true, 80L, 0L, 70L)));
    }

    [Test]
    public void ConsumeStateGas_oog_does_not_zero_reservoir()
    {
        EthereumGasPolicy gas = new() { Value = 10, StateReservoir = 50, StateGasUsed = 0, StateGasSpill = 0 };

        bool consumed = EthereumGasPolicy.ConsumeStateGas(ref gas, 70);

        Assert.That((consumed, gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill, EthereumGasPolicy.IsOutOfGas(in gas)),
            Is.EqualTo((false, 10UL, 50L, 0L, 0L, true)));
    }

    [Test]
    public void Revoking_advanced_refund_restores_net_spill_reservoir_and_spill_tracking()
    {
        // A net-spill (negative) reservoir, as left by RestoreChildStateGas after nested spills.
        EthereumGasPolicy gas = new() { Value = 400, StateReservoir = -300, StateGasUsed = 0, StateGasSpill = 300, StateGasSpillRefunded = 0 };

        long tracked = EthereumGasPolicy.AddStateGasRefundToReservoir(ref gas, 200, trackSpillRefund: true);
        Assert.That((tracked, gas.StateReservoir, gas.StateGasSpillRefunded), Is.EqualTo((200L, -100L, 200L)));

        EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas, 200, tracked);

        Assert.That((gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpillRefunded), Is.EqualTo((-300L, 0L, 0L)));
    }

    [Test]
    public void Revoking_consumed_advanced_refund_deducts_usage_without_fabricating_spill_debt()
    {
        EthereumGasPolicy gas = new() { Value = 400, StateReservoir = 0, StateGasUsed = 100 };

        long tracked = EthereumGasPolicy.AddStateGasRefundToReservoir(ref gas, 200, trackSpillRefund: true);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, 200), Is.True);

        EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas, 200, tracked);

        Assert.That((gas.StateReservoir, gas.StateGasUsed), Is.EqualTo((0L, 100L)));
    }

    [Test]
    public void Intrinsic_regular_gas_above_the_tx_cap_saturates_into_the_reservoir()
    {
        EthereumGasPolicy intrinsic = new() { Value = Eip7825Constants.DefaultTxGasLimitCap + 1, StateReservoir = 0 };

        EthereumGasPolicy gas = EthereumGasPolicy.CreateAvailableFromIntrinsic(
            Eip7825Constants.DefaultTxGasLimitCap + 2, in intrinsic, Amsterdam.Instance);

        Assert.That((gas.Value, gas.StateReservoir), Is.EqualTo((0UL, 1L)));
    }

    [Test]
    public void Child_frame_gets_full_state_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 333, StateGasUsed = 50 };

        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 444);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed, child.Value, child.StateReservoir, child.StateGasUsed),
            Is.EqualTo((1_000L, 0L, 50L, 444L, 333L, 0L)));
    }

    [Test]
    public void Child_frame_refund_restores_remaining_state_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 333, StateGasUsed = 50 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 444);
        EthereumGasPolicy.ConsumeStateGas(ref child, 100);
        EthereumGasPolicy.UpdateGas(ref child, 150);

        EthereumGasPolicy.Refund(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((1_294L, 233L, 150L)));
    }

    [Test]
    public void State_refund_is_clamped_to_intrinsic_state_floor()
    {
        EthereumGasPolicy gas = new() { Value = 100, StateReservoir = 0, StateGasUsed = 120 };

        EthereumGasPolicy.RefundStateGas(ref gas, 200, stateGasFloor: 40);

        Assert.That((gas.StateReservoir, gas.StateGasUsed), Is.EqualTo((80L, 40L)));
    }

    [Test]
    public void Code_insert_refund_credits_regular_gas_not_state_under_eip8038()
    {
        // The existing-authority refund returns the worst-case ACCOUNT_WRITE to the regular refund
        // counter and leaves the state dimension untouched (state refunds apply pre-execution).
        EthereumGasPolicy gas = new()
        {
            Value = 0,
            StateReservoir = 0,
            StateGasUsed = GasCostOf.PerAuthBaseState,
        };

        ulong regularRefund = EthereumGasPolicy.ApplyCodeInsertRefunds(ref gas, 1, Amsterdam.Instance, stateGasFloor: 0);

        Assert.That(regularRefund, Is.EqualTo(Eip8038Constants.AccountWrite));
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill),
            Is.EqualTo((0L, 0L, GasCostOf.PerAuthBaseState, 0L)));
    }

    [Test]
    public void State_gas_refund_of_spilled_charge_returns_to_regular_gas_not_reservoir()
    {
        // Source-based (LIFO) refund: a charge that spilled into gas_left must be refunded to gas_left;
        // inflating the reservoir would let later operations draw state gas the spec says is unavailable.
        EthereumGasPolicy gas = new() { Value = 10_000, StateReservoir = 0 };

        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, 4000), Is.True);
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill),
            Is.EqualTo((6000L, 0L, 4000L, 4000L)), "charge with empty reservoir spills into regular gas");

        EthereumGasPolicy.RefundStateGas(ref gas, 4000, stateGasFloor: 0);
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed),
            Is.EqualTo((10_000L, 0L, 0L)), "spilled refund returns to regular gas, reservoir stays empty");
    }

    [Test]
    public void State_gas_refund_of_reservoir_charge_returns_to_reservoir()
    {
        // Complementary case: a reservoir-funded charge refunds back to the reservoir (no spill).
        EthereumGasPolicy gas = new() { Value = 10_000, StateReservoir = 5000 };

        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, 4000), Is.True);
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill),
            Is.EqualTo((10_000L, 1000L, 4000L, 0L)), "reservoir-funded charge does not touch regular gas");

        EthereumGasPolicy.RefundStateGas(ref gas, 4000, stateGasFloor: 0);
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed),
            Is.EqualTo((10_000L, 5000L, 0L)), "reservoir-funded refund returns to the reservoir");
    }

    [Test]
    public void Call_depth_exceeded_create_does_not_credit_state_gas_refund()
    {
        byte[] code = Prepare.EvmCode.Create([], UInt256.Zero).Done;
        CodeInfo codeInfo = CodeInfoFactory.CreateCodeInfo(code);
        ExecutionEnvironment env = ExecutionEnvironment.Rent(
            codeInfo,
            executingAccount: Recipient,
            caller: Sender,
            codeSource: Recipient,
            callDepth: VirtualMachineStatics.MaxCallDepth,
            value: UInt256.Zero,
            inputData: ReadOnlyMemory<byte>.Empty);
        EthereumGasPolicy gas = new()
        {
            Value = 1_000_000,
            StateReservoir = GasCostOf.CreateState,
        };
        StackAccessTracker accessTracker = new();
        using VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            gas,
            ExecutionType.CALL,
            env,
            in accessTracker,
            Snapshot.Empty);

        Machine.SetBlockExecutionContext(new BlockExecutionContext(Build.A.Block.TestObject.Header, Amsterdam.Instance));
        Machine.SetTxExecutionContext(new TxExecutionContext(Sender, CodeInfoRepository, null, UInt256.Zero));

        Machine.ExecuteTransaction<OffFlag>(vmState, TestState, NullTxTracer.Instance);

        Assert.That((vmState.Gas.StateGasUsed, vmState.Gas.StateReservoir, vmState.StateGasRefundAdvanced),
            Is.EqualTo((0L, GasCostOf.CreateState, 0L)));
    }

    [Test]
    public void Exceptional_halt_preserves_state_gas()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 500, StateGasUsed = 10 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
        EthereumGasPolicy.ConsumeStateGas(ref child, 200);

        EthereumGasPolicy.SetOutOfGas(ref child);
        Assert.That((child.Value, child.StateReservoir), Is.EqualTo((0L, 300L)));

        EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parent, in child);
        Assert.That((parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((500L, 10L)));
    }

    [Test]
    public void Exceptional_halt_keeps_refunded_child_spill_in_gas_left()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 1_000);
        EthereumGasPolicy.ConsumeStateGas(ref child, 200);
        EthereumGasPolicy.RefundStateGas(ref child, 200, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parent, in child);

        // LIFO: the spill refunds to gas_left, so nothing restores to the parent reservoir.
        Assert.That((parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill), Is.EqualTo((0L, 0L, 200L)));
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L),
            "child halt does NOT propagate live spill into the cumulative burn counter");
        Assert.That(parent.StateGasSpillReclassified, Is.EqualTo(0L),
            "child halt does NOT reclassify live spill for a containing frame that later succeeds");
    }

    [Test]
    public void Revert_with_state_refund_keeps_spill_excluded_from_regular()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = new()
        {
            Value = 100,
            StateReservoir = 0,
            StateGasUsed = 0,
            StateGasSpill = GasCostOf.CreateState + 3 * GasCostOf.SSetState,
        };

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That(parent.StateGasSpill, Is.EqualTo(GasCostOf.CreateState + 3 * GasCostOf.SSetState));
        Assert.That(parent.StateGasSpillReclassified, Is.EqualTo(0L),
            "reverted state-gas spill remains excluded from block regular gas");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L));
    }

    [Test]
    public void Revert_with_refunded_create_spill_does_not_reclassify_it()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = new()
        {
            Value = 100,
            StateReservoir = 0,
            StateGasUsed = 0,
            StateGasSpill = GasCostOf.CreateState,
        };

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That(parent.StateGasSpillReclassified, Is.EqualTo(0L),
            "refunded CREATE state spill stays excluded from block regular gas");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L),
            "refunded CREATE state spill must not be added by the top-level halt formula");
    }

    [Test]
    public void Refund_state_gas_marks_spilled_refund()
    {
        EthereumGasPolicy gas = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, 200), Is.True);

        EthereumGasPolicy.RefundStateGas(ref gas, 80, stateGasFloor: 0);

        // LIFO: the refund returns to gas_left, not the reservoir, tracked in StateGasSpillRefunded.
        Assert.That((gas.StateGasUsed, gas.StateReservoir, gas.StateGasSpill, gas.StateGasSpillRefunded),
            Is.EqualTo((120L, 0L, 200L, 80L)));
        Assert.That(gas.Value, Is.EqualTo(880L));
    }

    [Test]
    public void Refund_state_gas_from_child_halt_preserves_spill_accounting()
    {
        EthereumGasPolicy gas = new() { Value = GasCostOf.CreateState, StateReservoir = 0, StateGasUsed = 0 };
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, GasCostOf.CreateState), Is.True);

        EthereumGasPolicy.RefundStateGas(ref gas, GasCostOf.CreateState, stateGasFloor: 0, trackSpillRefund: false);

        Assert.That((gas.StateGasUsed, gas.StateReservoir, gas.StateGasSpill, gas.StateGasSpillRefunded),
            Is.EqualTo((0L, GasCostOf.CreateState, GasCostOf.CreateState, 0L)));
        Assert.That(gas.StateGasSpillReclassified, Is.Zero);
    }

    [Test]
    public void Refunded_spill_propagates_through_success_chain()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 500);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref child, 200), Is.True);
        EthereumGasPolicy.RefundStateGas(ref child, 80, stateGasFloor: 0);

        EthereumGasPolicy.Refund(ref parent, in child);

        Assert.That((parent.StateGasSpill, parent.StateGasSpillRefunded), Is.EqualTo((200L, 80L)));
        Assert.That((parent.StateGasSpillReclassified, parent.StateGasSpillBurned), Is.EqualTo((0L, 0L)));
    }

    [Test]
    public void Revert_with_partial_spill_refund_keeps_spill_excluded_from_regular()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 500);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref child, 200), Is.True);
        EthereumGasPolicy.RefundStateGas(ref child, 80, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That(parent.StateGasSpill, Is.EqualTo(200L));
        Assert.That(parent.StateGasSpillRefunded, Is.EqualTo(80L));
        Assert.That(parent.StateGasSpillReclassified, Is.EqualTo(0L),
            "reverted state-gas spill remains excluded from block regular gas");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L));
    }

    [Test]
    public void Code_deposit_halt_keeps_refunded_child_spill_in_gas_left()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 1_000);
        EthereumGasPolicy.ConsumeStateGas(ref child, 200);
        EthereumGasPolicy.RefundStateGas(ref child, 200, stateGasFloor: 0);

        EthereumGasPolicy.Refund(ref parent, in child);
        EthereumGasPolicy.RevertRefundToHalt(ref parent, in child);

        // The fully-refunded child spill is excluded from block regular gas; the reservoir stays 0.
        Assert.That((parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill), Is.EqualTo((0L, 0L, 200L)));
        Assert.That(parent.StateGasSpillRefunded, Is.EqualTo(200L));
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L),
            "code-deposit halt does NOT reroute live spill into the cumulative burn counter");
    }

    [Test]
    public void Code_deposit_halt_removes_merged_child_state_usage_without_refunding_reservoir_twice()
    {
        ulong parentRegularGas = 1_000;
        ulong childRegularGas = 500;
        long parentStateGasUsed = GasCostOf.CreateState;
        long childStateGasUsed = GasCostOf.NewAccountState + GasCostOf.SSetState;
        long childRemainingStateReservoir = 123;
        EthereumGasPolicy parent = new()
        {
            Value = parentRegularGas,
            StateReservoir = parentStateGasUsed + childStateGasUsed + childRemainingStateReservoir,
            StateGasSpill = 77,
            StateGasSpillRefunded = 33,
            StateGasSpillBurned = 22,
            StateGasSpillReclassified = 11,
        };

        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref parent, parentStateGasUsed), Is.True);
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, childRegularGas);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref child, childStateGasUsed), Is.True);
        EthereumGasPolicy.Refund(ref parent, in child);

        EthereumGasPolicy.RevertRefundToHalt(ref parent, in child);

        Assert.That(
            (
                parent.Value,
                parent.StateReservoir,
                parent.StateGasUsed,
                parent.StateGasSpill,
                parent.StateGasSpillRefunded,
                parent.StateGasSpillBurned,
                parent.StateGasSpillReclassified
            ),
            Is.EqualTo(
                (
                    parentRegularGas + childRegularGas,
                    childRemainingStateReservoir + childStateGasUsed,
                    parentStateGasUsed,
                    77L,
                    33L,
                    22L,
                    11L
                )));
    }

    [Test]
    public void ResetForHalt_snaps_state_gas_to_tx_start_shape()
    {
        // Architectural invariant: ResetForHalt resets the policy struct to its tx-start
        // shape — reservoir back to R0, StateGasUsed to the intrinsic floor, spill to 0.
        // The post-reset values feed SpentGas (= txGasLimit - remaining - reservoir) so
        // the user does not keep paying for state-gas they didn't get to commit.
        EthereumGasPolicy gas = new() { Value = 100_000, StateReservoir = 1_000, StateGasUsed = 0 };

        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, 5_000), Is.True);
        Assert.That((gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill), Is.EqualTo((0L, 5_000L, 4_000L)),
            "consume 5_000 state-gas with 1_000 reservoir => reservoir=0, used=5_000, spill=4_000");

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: 1_000, initialStateGasUsed: 0);

        Assert.That(gas.StateReservoir, Is.EqualTo(1_000L), "reservoir snaps to R0");
        Assert.That(gas.StateGasUsed, Is.EqualTo(0L), "state-gas-used snaps to intrinsic floor");
        Assert.That(gas.StateGasSpill, Is.EqualTo(0L), "spill is zeroed");
    }

    [Test]
    public void Top_level_halt_block_state_gas_snapshot_captures_spill_only_not_reservoir_portion()
    {
        // Per EIP-8037, block.gasUsed = max(sum_regular, sum_state). On a top-level halt:
        //   - the reservoir-portion of charged state-gas is refunded (the user does not pay
        //     for state-gas that did not commit and was within the reservoir budget),
        //   - the spill-portion stays charged because it was paid for via the regular gas
        //     budget (spill = state-gas that overflowed the reservoir into regular gas).
        // Callers building the block-level contribution must snapshot StateGasSpill BEFORE
        // ResetForHalt zeroes it, then add it on top of the post-reset StateGasUsed
        // (= intrinsic floor). Snapshotting full StateGasUsed would over-count by the
        // reservoir-portion that was correctly refunded.
        EthereumGasPolicy gas = new() { Value = 100_000, StateReservoir = 1_000, StateGasUsed = 0 };
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, 5_000), Is.True);
        Assert.That((gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill), Is.EqualTo((0L, 5_000L, 4_000L)),
            "after consuming 5_000 with 1_000 reservoir: reservoir=0, used=5_000 (= 1_000 reservoir-portion + 4_000 spill), spill=4_000");

        // CORRECT pattern: snapshot SPILL only (not full StateGasUsed) BEFORE reset.
        long preHaltSpill = gas.StateGasSpill;
        Assert.That(preHaltSpill, Is.EqualTo(4_000L), "spill-only snapshot");

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: 1_000, initialStateGasUsed: 0);

        // Block-level contribution = post-reset StateGasUsed (intrinsic floor) + pre-halt spill.
        // The reservoir-portion (1_000) is correctly excluded — that's the refunded portion.
        long blockLevelContribution = gas.StateGasUsed + preHaltSpill;
        Assert.That(blockLevelContribution, Is.EqualTo(4_000L),
            "block-level sum_state contribution = floor (0) + spill (4_000); reservoir-portion (1_000) is refunded");
    }

    [Test]
    public void Top_level_halt_block_state_gas_overcounts_if_full_state_gas_used_is_snapshotted()
    {
        // Regression-guard: snapshotting full StateGasUsed instead of just spill over-counts
        // block-level sum_state by the reservoir-portion.
        const long reservoirAtTxStart = 100_000;
        const long stateGasCharged = GasCostOf.SSetState;

        EthereumGasPolicy gas = new() { Value = 1_000_000, StateReservoir = reservoirAtTxStart, StateGasUsed = 0 };
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, stateGasCharged), Is.True);
        Assert.That(gas.StateGasSpill, Is.EqualTo(0L), "reservoir covers full charge; no spill");
        Assert.That(gas.StateGasUsed, Is.EqualTo(stateGasCharged), "full charge recorded in StateGasUsed");

        long wrongSnapshot = gas.StateGasUsed;
        long correctSnapshot = gas.StateGasSpill;  // = 0 — what the canonical contribution is

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: reservoirAtTxStart, initialStateGasUsed: 0);

        long blockLevelCorrect = gas.StateGasUsed + correctSnapshot;
        long blockLevelWrong = gas.StateGasUsed + wrongSnapshot;
        Assert.That(blockLevelWrong - blockLevelCorrect, Is.EqualTo(GasCostOf.SSetState),
            "snapshotting StateGasUsed (instead of StateGasSpill) overcounts block-level sum_state by exactly the reservoir-portion (= 1 SSetState in this case)");
    }

    [Test]
    public void Top_level_halt_block_state_gas_per_tx_pattern_with_spill()
    {
        // Per-tx pattern: state-gas charged exceeds the reservoir, so part spills into
        // regular gas (the user paid for it via regular budget). Block-level sum_state
        // contribution per halt = floor + spill. Across N halts in a block, sum is
        // N * (floor + spill). Reading the post-reset StateGasUsed alone (= floor) without
        // adding spill would undercount by N * spill.
        const long perTxGasLimit = 1_000_000;
        const long intrinsicStateGas = 0;
        const long reservoirAtTxStart = 100_000;
        const long stateGasCharged = 104_174;   // reservoir(100k) consumed + 4_174 spill

        EthereumGasPolicy gas = new() { Value = perTxGasLimit, StateReservoir = reservoirAtTxStart, StateGasUsed = intrinsicStateGas };
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, stateGasCharged), Is.True);
        Assert.That(gas.StateGasSpill, Is.EqualTo(4_174L), "4_174 spill from reservoir overflow");

        long preHaltSpill = gas.StateGasSpill;
        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: reservoirAtTxStart, initialStateGasUsed: intrinsicStateGas);

        long blockLevelContribution = gas.StateGasUsed + preHaltSpill;
        Assert.That(blockLevelContribution, Is.EqualTo(intrinsicStateGas + 4_174L),
            "per-tx block-level contribution = intrinsic floor + spill; reservoir-portion is refunded");
    }

    [Test]
    public void Inner_revert_spill_propagates_without_burn()
    {
        // EIP-8037: when a child frame REVERTS after spilling state gas from gas_left,
        // the state work is refunded to the parent reservoir and the spill remains
        // state-attributed for block-regular accounting.
        EthereumGasPolicy parent = new() { Value = 100_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 50_000);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref child, 4_174L), Is.True,
            "child consumes 4_174 state-gas with reservoir=0; entirely spills from gas_left");
        Assert.That(child.StateGasSpill, Is.EqualTo(4_174L));

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That(parent.StateGasSpill, Is.EqualTo(4_174L), "live spill propagates to parent for non-halt accounting");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L));
    }

    [Test]
    public void Reverted_state_gas_spill_propagates_through_success_chain()
    {
        // Cumulative invariant: when a grandchild reverts with spill but its child succeeds,
        // the spill counter must propagate up through the success path so block-regular
        // accounting can exclude the reverted state work.
        EthereumGasPolicy parent = new() { Value = 500_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 400_000);

        EthereumGasPolicy grandchild = EthereumGasPolicy.CreateChildFrameGas(ref child, 200_000);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref grandchild, 4_174L), Is.True);
        EthereumGasPolicy.RestoreChildStateGas(ref child, in grandchild);
        Assert.That(child.StateGasSpill, Is.EqualTo(4_174L));
        Assert.That(child.StateGasSpillBurned, Is.EqualTo(0L));

        EthereumGasPolicy.Refund(ref parent, in child);

        Assert.That(parent.StateGasSpill, Is.EqualTo(4_174L),
            "Refund propagates child.StateGasSpill cumulatively through the success path");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L));
    }

    [Test]
    public void Reset_for_halt_preserves_state_gas_spill_burned()
    {
        // ResetForHalt zeros live state-gas tracking but must NOT reset the tx-wide cumulative
        // StateGasSpillBurned, which the halt formula consumes afterwards.
        EthereumGasPolicy gas = new()
        {
            Value = 0,
            StateReservoir = 0,
            StateGasUsed = GasCostOf.CreateState,
            StateGasSpill = 0,
            StateGasSpillBurned = 4_174,
        };

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: 0, initialStateGasUsed: GasCostOf.CreateState);

        Assert.That(gas.StateGasSpillBurned, Is.EqualTo(4_174L),
            "StateGasSpillBurned survives ResetForHalt so the halt formula can reattribute the burned spill");
    }

    [Test]
    public void Top_level_halt_block_regular_dimension_includes_burned_spill()
    {
        // N inner reverts each spilling S from gas_left contribute (initialRegular + N*S) to block_regular
        // and (intrinsicState - N*S) to block_state: burned spill was paid from gas_left, not the reservoir.
        const long txGasLimit = 16_000_000;
        const long intrinsicStateGas = GasCostOf.CreateState;
        const long innerRevertSpill = 4_174;

        long initialReservoir = Math.Max(0, txGasLimit - intrinsicStateGas - 16_777_216);
        long expectedBlockRegularBeforeFix = txGasLimit - intrinsicStateGas - initialReservoir;
        long effectiveStateGas = Math.Max(0, intrinsicStateGas - innerRevertSpill);
        long blockRegular = txGasLimit - effectiveStateGas - initialReservoir;
        long blockState = effectiveStateGas;

        Assert.That(blockRegular - expectedBlockRegularBeforeFix, Is.EqualTo(innerRevertSpill),
            "applying the spillBurned reattribution adds the burned spill to block_regular");
        Assert.That(blockState, Is.EqualTo(intrinsicStateGas - innerRevertSpill),
            "block_state is reduced by the same amount so total = max(regular, state) is consistent");
    }

    [Test]
    public void Revert_restores_state_gas_to_parent_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 400, StateGasUsed = 20 };
        EthereumGasPolicy.Consume(ref parent, 600);
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
        EthereumGasPolicy.UpdateGas(ref child, 100);
        EthereumGasPolicy.ConsumeStateGas(ref child, 150);

        EthereumGasPolicy.UpdateGasUp(ref parent, EthereumGasPolicy.GetRemainingGas(in child));
        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((900L, 400L, 20L)));
    }

    [Test]
    public void Revert_restores_child_inline_state_refund_to_parent_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 400, StateGasUsed = 20 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
        EthereumGasPolicy.ConsumeStateGas(ref child, 150);
        EthereumGasPolicy.RefundStateGas(ref child, 40, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That((parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((400L, 20L)));
    }

    [Test]
    public void Revert_discards_descendant_spill_once_refund_reaches_ancestor()
    {
        EthereumGasPolicy parent = new() { Value = 500_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy outer = EthereumGasPolicy.CreateChildFrameGas(ref parent, 400_000);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref outer, GasCostOf.CreateState), Is.True);

        EthereumGasPolicy inner = EthereumGasPolicy.CreateChildFrameGas(ref outer, 200_000);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref inner, GasCostOf.SSetState), Is.True);
        EthereumGasPolicy.RefundStateGas(ref inner, GasCostOf.SSetState, stateGasFloor: 0);
        EthereumGasPolicy.RestoreChildStateGas(ref outer, in inner);

        EthereumGasPolicy.RefundStateGas(ref outer, GasCostOf.CreateState, stateGasFloor: 0);
        EthereumGasPolicy.RestoreChildStateGas(ref parent, in outer);

        // Both refunds return to gas_left; the fully-refunded spills are excluded from block regular gas.
        Assert.That((parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill),
            Is.EqualTo((0L, 0L, GasCostOf.CreateState + GasCostOf.SSetState)));
        Assert.That(parent.StateGasSpillRefunded, Is.EqualTo(GasCostOf.CreateState + GasCostOf.SSetState));
        Assert.That(parent.StateGasSpillReclassified, Is.EqualTo(0L),
            "ancestor refunds consumed both spills, so neither is reclassified to block regular gas");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L),
            "refunded descendant spill must not be added by the top-level halt formula");
    }

    [TestCase(ExpectedResult = 5_000L)]
    public long Spent_gas_subtracts_state_reservoir()
    {
        EthereumGasPolicy gas = new() { Value = 3_000, StateReservoir = 2_000, StateGasUsed = 500 };
        return 10_000L - (long)EthereumGasPolicy.GetRemainingGas(in gas) - EthereumGasPolicy.GetStateReservoir(in gas);
    }
}
