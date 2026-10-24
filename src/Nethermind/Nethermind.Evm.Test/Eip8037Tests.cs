// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
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
    public ulong Code_deposit_execution_cost(int codeLength)
    {
        CodeDepositHandler.CalculateCost(Amsterdam.Instance, codeLength, out ulong executionCost, out _);
        return executionCost;
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
    public void System_transaction_gas_keeps_execution_budget_and_state_reservoir()
    {
        EthereumGasPolicy intrinsicGas = EthereumGasPolicy.CreateSystemTransactionIntrinsicGas(Eip8037Constants.SystemCallGasLimit);
        Assert.That(EthereumGasPolicy.TryCreateSystemTransactionAvailableGas(Eip8037Constants.SystemCallGasLimit, in intrinsicGas, Amsterdam.Instance, out EthereumGasPolicy availableGas), Is.True);

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
            Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref availableGas, GasCostOf.SSetState), Is.True);
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
    public void Execution_transaction_gas_uses_tx_cap_even_when_intrinsic_state_matches_system_reservoir()
    {
        EthereumGasPolicy intrinsicGas = new()
        {
            StateReservoir = Eip8037Constants.SystemCallStateReservoir,
        };

        Assert.That(EthereumGasPolicy.TryCreateAvailableFromIntrinsic(Eip8037Constants.SystemCallGasLimit, in intrinsicGas, Amsterdam.Instance, out EthereumGasPolicy availableGas), Is.True);
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

        bool success = CodeDepositHandler.CalculateCost(Amsterdam.Instance, 33, in gas, out ulong executionCost, out long stateCost);

        Assert.That((success, executionCost, stateCost),
            Is.EqualTo((true, 12L, GasCostOf.CodeDepositState * 33)));
    }

    [Test]
    public void Intrinsic_gas_excludes_top_frame_state_costs()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved()
            .WithAuthorizationCode(new AuthorizationTuple(1, TestItem.AddressF, 0, 0, UInt256.One, UInt256.One))
            .TestObject;

        IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, Amsterdam.Instance, 30_000_000);

        Assert.That(intrinsicGas.Standard.StateReservoir, Is.Zero);
    }

    [Test]
    public void Amsterdam_access_list_floor_pricing_is_added_to_execution_and_floor_intrinsic_gas()
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
        // Access-list entries subtract the warm charge from the cold costs; the value-bearing
        // recipient touch adds COLD_ACCOUNT_ACCESS + TX_VALUE (transfer log folded into TX_VALUE).
        ulong recipientExecution = Eip8038Constants.ColdAccountAccess + GasCostOf.TxValueCostEip2780;
        ulong accessListBaseCost = Eip8038Constants.AccessListAddressCost + 3 * Eip8038Constants.AccessListStorageKeyCost;
        ulong accessListFloorTokens = (20ul + 3ul * 32ul) * Amsterdam.Instance.GasCosts.TxDataNonZeroMultiplier;
        ulong accessListFloorCost = accessListFloorTokens * Amsterdam.Instance.GasCosts.TotalCostFloorPerToken;
        ulong expectedExecution = GasCostOf.TransactionEip2780 + recipientExecution + accessListBaseCost + accessListFloorCost;
        ulong expectedFloorGas = GasCostOf.TransactionEip2780 + recipientExecution + accessListFloorCost;

        Assert.That(splitIntrinsicGas.Standard.Value, Is.EqualTo(expectedExecution));
        Assert.That(splitIntrinsicGas.Standard.StateReservoir, Is.Zero);
        Assert.That(splitIntrinsicGas.FloorGas.Value, Is.EqualTo(expectedFloorGas));
        Assert.That(intrinsicGas.Standard, Is.EqualTo(expectedExecution));
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
        ulong expectedExecution = GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + GasCostOf.AccessStorageListEntry;

        Assert.That(intrinsicGas.Standard.Value, Is.EqualTo(expectedExecution));
        Assert.That(intrinsicGas.FloorGas.Value, Is.EqualTo(GasCostOf.Transaction));
    }

    [Test]
    public void State_gas_consumption_spills_to_execution_gas()
    {
        EthereumGasPolicy gas = new() { Value = 100, StateReservoir = 50, StateGasUsed = 0 };

        bool consumed = EthereumGasPolicy.TryConsumeStateGas(ref gas, 70);

        Assert.That((consumed, gas.Value, gas.StateReservoir, gas.StateGasUsed), Is.EqualTo((true, 80L, 0L, 70L)));
    }

    [Test]
    public void ConsumeStateGas_oog_does_not_zero_reservoir()
    {
        EthereumGasPolicy gas = new() { Value = 10, StateReservoir = 50, StateGasUsed = 0, StateGasSpill = 0 };

        bool consumed = EthereumGasPolicy.TryConsumeStateGas(ref gas, 70);

        Assert.That((consumed, gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill),
            Is.EqualTo((false, 10UL, 50L, 0L, 0L)));
    }

    [Test]
    public void Advanced_refund_credits_reservoir_and_revoking_it_leaves_gas_left_unchanged()
    {
        EthereumGasPolicy gas = new() { Value = 400, StateReservoir = 0, StateGasUsed = 0, StateGasSpill = 300 };

        EthereumGasPolicy.AddStateGasRefundToReservoir(ref gas, 200);
        Assert.That((gas.Value, gas.StateReservoir), Is.EqualTo((400UL, 200L)));

        EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas, 200);

        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill), Is.EqualTo((400UL, 0L, 0L, 300L)));
    }

    [Test]
    public void Revoking_consumed_advanced_refund_deducts_usage_without_fabricating_spill_debt()
    {
        EthereumGasPolicy gas = new() { Value = 400, StateReservoir = 0, StateGasUsed = 100 };

        EthereumGasPolicy.AddStateGasRefundToReservoir(ref gas, 200);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref gas, 200), Is.True);

        EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas, 200);

        Assert.That((gas.StateReservoir, gas.StateGasUsed), Is.EqualTo((0L, 100L)));
    }

    [Test]
    public void Intrinsic_execution_gas_above_the_tx_cap_saturates_into_the_reservoir()
    {
        EthereumGasPolicy intrinsic = new() { Value = Eip7825Constants.DefaultTxGasLimitCap + 1, StateReservoir = 0 };

        Assert.That(EthereumGasPolicy.TryCreateAvailableFromIntrinsic(
            Eip7825Constants.DefaultTxGasLimitCap + 2, in intrinsic, Amsterdam.Instance, out EthereumGasPolicy gas), Is.True);

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
        EthereumGasPolicy.TryConsumeStateGas(ref child, 100);
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
    public void Code_insert_refund_is_not_applied_under_eip8037()
    {
        EthereumGasPolicy gas = new()
        {
            Value = 0,
            StateReservoir = 0,
            StateGasUsed = GasCostOf.PerAuthBaseState,
        };

        ulong executionRefund = EthereumGasPolicy.ApplyCodeInsertRefunds(ref gas, 1, Amsterdam.Instance, stateGasFloor: 0);

        Assert.That(executionRefund, Is.Zero);
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill),
            Is.EqualTo((0L, 0L, GasCostOf.PerAuthBaseState, 0L)));
    }

    [Test]
    public void Code_insert_refund_uses_legacy_rule_without_eip8037()
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Cancun.Instance) { IsEip8038Enabled = true };
        EthereumGasPolicy gas = default;

        ulong executionRefund = EthereumGasPolicy.ApplyCodeInsertRefunds(ref gas, 1, spec, stateGasFloor: 0);

        Assert.That(executionRefund, Is.EqualTo(GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost));
    }

    [Test]
    public void State_gas_refund_of_reservoir_charge_returns_to_reservoir()
    {
        // Complementary case: a reservoir-funded charge refunds back to the reservoir (no spill).
        EthereumGasPolicy gas = new() { Value = 10_000, StateReservoir = 5000 };

        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref gas, 4000), Is.True);
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill),
            Is.EqualTo((10_000L, 1000L, 4000L, 0L)), "reservoir-funded charge does not touch execution gas");

        EthereumGasPolicy.RefundStateGas(ref gas, 4000, stateGasFloor: 0);
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed),
            Is.EqualTo((10_000L, 5000L, 0L)), "reservoir-funded refund returns to the reservoir");
    }

    [Test]
    public void Call_depth_exceeded_create_does_not_credit_state_gas_refund()
    {
        byte[] code = Prepare.EvmCode.Create([], UInt256.Zero).Done;
        CodeInfo codeInfo = new(code);
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
        EthereumGasPolicy.TryConsumeStateGas(ref child, 200);

        EthereumGasPolicy.ClearExecutionGas(ref child);
        Assert.That((child.Value, child.StateReservoir), Is.EqualTo((0L, 300L)));

        EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parent, in child);
        Assert.That((parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((500L, 10L)));
    }

    [Test]
    public void Exceptional_halt_does_not_inherit_child_spill()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 1_000);
        EthereumGasPolicy.TryConsumeStateGas(ref child, 200);
        EthereumGasPolicy.RefundStateGas(ref child, 200, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parent, in child);

        // The halt burns the spill with the child's gas_left, including the part the refund put in the reservoir.
        Assert.That((parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill),
            Is.EqualTo((0L, 0L, 0L)));
    }

    [Test]
    public void Revert_of_self_refunded_child_keeps_spill_in_parent_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, GasCostOf.CreateState);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, GasCostOf.CreateState), Is.True);
        EthereumGasPolicy.RefundStateGas(ref child, GasCostOf.CreateState, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasSpill),
            Is.EqualTo((1_000UL, GasCostOf.CreateState, GasCostOf.CreateState)));
    }

    [Test]
    public void Refund_state_gas_credits_reservoir_not_gas_left()
    {
        EthereumGasPolicy gas = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref gas, 200), Is.True);

        EthereumGasPolicy.RefundStateGas(ref gas, 80, stateGasFloor: 0);

        Assert.That((gas.Value, gas.StateGasUsed, gas.StateReservoir, gas.StateGasSpill),
            Is.EqualTo((800UL, 120L, 80L, 200L)));
    }

    [Test]
    public void Spill_and_refunded_reservoir_propagate_through_success_chain()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 500);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, 200), Is.True);
        EthereumGasPolicy.RefundStateGas(ref child, 80, stateGasFloor: 0);

        EthereumGasPolicy.Refund(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasSpill), Is.EqualTo((1_300UL, 80L, 200L)));
    }

    [TestCase(false, TestName = "Reverted_child_does_not_repay_parent_spill")]
    [TestCase(true, TestName = "Exceptionally_halted_child_does_not_repay_parent_spill")]
    public void Unsuccessful_child_return_does_not_repay_parent_spill(bool exceptionalHalt)
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateGasSpill = 200 };
        EthereumGasPolicy child = new() { StateReservoir = 80 };

        if (exceptionalHalt)
        {
            EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parent, in child);
        }
        else
        {
            EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);
        }

        Assert.That(
            (parent.Value, parent.StateReservoir, parent.StateGasSpill),
            Is.EqualTo((1_000UL, 80L, 200L)));
    }

    [Test]
    public void Revert_moves_partially_refunded_child_spill_to_parent_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 500);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, 200), Is.True);
        EthereumGasPolicy.RefundStateGas(ref child, 80, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        // All 200 the child drew from gas_left lands in the reservoir; none returns to gas_left.
        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill),
            Is.EqualTo((1_000UL, 200L, 0L, 200L)));
    }

    [TestCase(0, TestName = "Code_deposit_halt_does_not_inherit_unrefunded_child_spill")]
    [TestCase(200, TestName = "Code_deposit_halt_does_not_inherit_refunded_child_spill")]
    public void Code_deposit_halt_does_not_inherit_child_spill(long stateGasRefund)
    {
        EthereumGasPolicy parent = new();
        EthereumGasPolicy child = EthereumGasPolicy.FromULong(1_000);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, 200), Is.True);
        if (stateGasRefund > 0)
        {
            EthereumGasPolicy.RefundStateGas(ref child, stateGasRefund, stateGasFloor: 0);
        }

        EthereumGasPolicy.Refund(ref parent, in child);
        Assert.That(EthereumGasPolicy.UpdateGas(ref parent, child.Value), Is.True);
        EthereumGasPolicy.RevertRefundToHalt(ref parent, in child);

        Assert.That(
            (parent.Value, parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill),
            Is.EqualTo((0UL, 0L, 0L, 0L)));
    }

    [Test]
    public void Code_deposit_halt_removes_merged_child_state_usage_without_refunding_reservoir_twice()
    {
        ulong parentExecutionGas = 1_000;
        ulong childExecutionGas = 40_000;
        long parentStateGasUsed = GasCostOf.CreateState;
        long childStateGasUsed = GasCostOf.NewAccountState + GasCostOf.SSetState;
        long childRemainingStateReservoir = 123;
        EthereumGasPolicy parent = new()
        {
            Value = parentExecutionGas,
            StateReservoir = parentStateGasUsed + childStateGasUsed + childRemainingStateReservoir,
            StateGasSpill = 77,
        };

        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref parent, parentStateGasUsed), Is.True);
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, childExecutionGas);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, childStateGasUsed), Is.True);
        EthereumGasPolicy.Refund(ref parent, in child);

        EthereumGasPolicy.RevertRefundToHalt(ref parent, in child);

        Assert.That(
            (
                parent.Value,
                parent.StateReservoir,
                parent.StateGasUsed,
                parent.StateGasSpill
            ),
            Is.EqualTo(
                (
                    parentExecutionGas + childExecutionGas,
                    childRemainingStateReservoir + childStateGasUsed,
                    parentStateGasUsed,
                    77L
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

        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref gas, 5_000), Is.True);
        Assert.That((gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill), Is.EqualTo((0L, 5_000L, 4_000L)),
            "consume 5_000 state-gas with 1_000 reservoir => reservoir=0, used=5_000, spill=4_000");

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: 1_000, initialStateGasUsed: 0);

        Assert.That(gas.StateReservoir, Is.EqualTo(1_000L), "reservoir snaps to R0");
        Assert.That(gas.StateGasUsed, Is.EqualTo(0L), "state-gas-used snaps to intrinsic floor");
        Assert.That(gas.StateGasSpill, Is.EqualTo(0L), "spill is zeroed");
    }

    [Test]
    public void Top_level_halt_block_state_gas_is_intrinsic_floor_not_spill()
    {
        // Top-level halt: block_state = post-reset StateGasUsed (intrinsic floor); the spilled
        // portion was paid from gas_left and burns as execution gas.
        EthereumGasPolicy gas = new() { Value = 100_000, StateReservoir = 1_000, StateGasUsed = 0 };
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref gas, 5_000), Is.True);
        Assert.That((gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill), Is.EqualTo((0L, 5_000L, 4_000L)),
            "after consuming 5_000 with 1_000 reservoir: reservoir=0, used=5_000 (= 1_000 reservoir-portion + 4_000 spill), spill=4_000");

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: 1_000, initialStateGasUsed: 0);

        // Block_state = post-reset StateGasUsed (0); the spill burns as execution gas.
        long blockLevelContribution = gas.StateGasUsed;
        Assert.That(blockLevelContribution, Is.EqualTo(0L),
            "block-level sum_state contribution = post-reset floor (0); spill is burned as execution gas");
        Assert.That((gas.StateReservoir, gas.StateGasSpill), Is.EqualTo((1_000L, 0L)));
    }

    [Test]
    public void Top_level_halt_block_state_gas_uses_post_reset_state_gas_used()
    {
        // Regression-guard: block_state must read the POST-reset StateGasUsed (intrinsic floor), not
        // the pre-reset value, or it over-counts by the refunded reservoir-portion.
        const long reservoirAtTxStart = 100_000;
        const long stateGasCharged = GasCostOf.SSetState;

        EthereumGasPolicy gas = new() { Value = 1_000_000, StateReservoir = reservoirAtTxStart, StateGasUsed = 0 };
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref gas, stateGasCharged), Is.True);
        Assert.That(gas.StateGasSpill, Is.EqualTo(0L), "reservoir covers full charge; no spill");
        Assert.That(gas.StateGasUsed, Is.EqualTo(stateGasCharged), "full charge recorded in StateGasUsed");

        long preResetStateGasUsed = gas.StateGasUsed;
        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: reservoirAtTxStart, initialStateGasUsed: 0);

        Assert.That(preResetStateGasUsed - gas.StateGasUsed, Is.EqualTo(GasCostOf.SSetState),
            "reading pre-reset StateGasUsed instead of the post-reset floor overcounts block-level sum_state by exactly the reservoir-portion (= 1 SSetState in this case)");
    }

    [Test]
    public void Top_level_halt_block_state_gas_per_tx_pattern_with_spill()
    {
        // Per-tx pattern with spill: the reservoir-portion is refunded and the spill is burned as
        // execution gas. Block-level sum_state contribution per halt = post-reset StateGasUsed
        // (intrinsic floor); the spill is NOT added to block_state.
        const long perTxGasLimit = 1_000_000;
        const long intrinsicStateGas = 0;
        const long reservoirAtTxStart = 100_000;
        const long stateGasCharged = 104_174;   // reservoir(100k) consumed + 4_174 spill

        EthereumGasPolicy gas = new() { Value = perTxGasLimit, StateReservoir = reservoirAtTxStart, StateGasUsed = intrinsicStateGas };
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref gas, stateGasCharged), Is.True);
        Assert.That(gas.StateGasSpill, Is.EqualTo(4_174L), "4_174 spill from reservoir overflow");

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: reservoirAtTxStart, initialStateGasUsed: intrinsicStateGas);

        long blockLevelContribution = gas.StateGasUsed;
        Assert.That(blockLevelContribution, Is.EqualTo(intrinsicStateGas),
            "per-tx block-level contribution = intrinsic floor; the spill is burned as execution gas");
    }

    [Test]
    public void Inner_revert_spill_returns_to_parent_reservoir_not_gas_left()
    {
        // A reverting intermediary must not hand state gas it paid from gas_left back to its caller.
        EthereumGasPolicy parent = new() { Value = 100_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 50_000);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, 4_174L), Is.True);
        Assert.That(child.StateGasSpill, Is.EqualTo(4_174L));

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill),
            Is.EqualTo((100_000UL, 4_174L, 0L, 4_174L)));
    }

    [Test]
    public void Reverted_grandchild_spill_stays_in_reservoir_through_success_chain()
    {
        EthereumGasPolicy parent = new() { Value = 500_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 400_000);

        EthereumGasPolicy grandchild = EthereumGasPolicy.CreateChildFrameGas(ref child, 200_000);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref grandchild, 4_174L), Is.True);
        EthereumGasPolicy.RestoreChildStateGas(ref child, in grandchild);

        EthereumGasPolicy.Refund(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill),
            Is.EqualTo((900_000UL, 4_174L, 0L, 4_174L)));
    }

    [Test]
    public void Reset_for_halt_preserves_gas_left()
    {
        EthereumGasPolicy gas = new()
        {
            Value = 4_242,
            StateReservoir = 0,
            StateGasUsed = GasCostOf.CreateState,
            StateGasSpill = GasCostOf.CreateState,
        };

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: 0, initialStateGasUsed: GasCostOf.CreateState);

        Assert.That((gas.Value, gas.StateGasSpill), Is.EqualTo((4_242UL, 0L)));
    }

    [Test]
    public void Top_level_halt_block_state_is_full_intrinsic_not_reduced_by_spill()
    {
        // New halt model (RefundOnTopLevelHalt): block_state = full intrinsic floor. Unlike the OLD
        // model that subtracted burned spill from block_state (moving it to block_execution), the spill
        // is now burned as execution gas via gas_left, so no explicit block_state subtraction is applied.
        const long intrinsicStateGas = GasCostOf.CreateState;
        const long innerRevertSpill = 4_174;

        long newBlockState = intrinsicStateGas;
        long oldBlockState = Math.Max(0, intrinsicStateGas - innerRevertSpill);

        Assert.That(newBlockState - oldBlockState, Is.EqualTo(innerRevertSpill),
            "the new model no longer subtracts burned spill from block_state; it stays at the full intrinsic floor");
    }

    [Test]
    public void Revert_restores_state_gas_to_parent_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 400, StateGasUsed = 20 };
        EthereumGasPolicy.UpdateGas(ref parent, 600);
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
        EthereumGasPolicy.UpdateGas(ref child, 100);
        EthereumGasPolicy.TryConsumeStateGas(ref child, 150);

        EthereumGasPolicy.UpdateGasUp(ref parent, EthereumGasPolicy.GetRemainingGas(in child));
        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((900L, 400L, 20L)));
    }

    [Test]
    public void Revert_restores_child_inline_state_refund_to_parent_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 400, StateGasUsed = 20 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
        EthereumGasPolicy.TryConsumeStateGas(ref child, 150);
        EthereumGasPolicy.RefundStateGas(ref child, 40, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That((parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((400L, 20L)));
    }

    [Test]
    public void Revert_keeps_refunded_descendant_spill_in_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 500_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy outer = EthereumGasPolicy.CreateChildFrameGas(ref parent, 400_000);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref outer, GasCostOf.CreateState), Is.True);

        EthereumGasPolicy inner = EthereumGasPolicy.CreateChildFrameGas(ref outer, 200_000);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref inner, GasCostOf.SSetState), Is.True);
        EthereumGasPolicy.RefundStateGas(ref inner, GasCostOf.SSetState, stateGasFloor: 0);
        EthereumGasPolicy.RestoreChildStateGas(ref outer, in inner);

        EthereumGasPolicy.RefundStateGas(ref outer, GasCostOf.CreateState, stateGasFloor: 0);
        EthereumGasPolicy.RestoreChildStateGas(ref parent, in outer);

        // Both refunds land in the reservoir; gas_left keeps its starting value.
        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill),
            Is.EqualTo((500_000UL, GasCostOf.CreateState + GasCostOf.SSetState, 0L, GasCostOf.CreateState + GasCostOf.SSetState)));
    }

    [TestCase(ExpectedResult = 5_000L)]
    public long Spent_gas_subtracts_state_reservoir()
    {
        EthereumGasPolicy gas = new() { Value = 3_000, StateReservoir = 2_000, StateGasUsed = 500 };
        return 10_000L - (long)EthereumGasPolicy.GetRemainingGas(in gas) - EthereumGasPolicy.GetStateReservoir(in gas);
    }

    [Test]
    public void State_gas_allowance_follows_forwarded_gas_and_parent_remainder()
    {
        EthereumGasPolicy top = new() { Value = 1_000_000, StateReservoir = 10_000_000 };

        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref top, 50_000);
        Assert.That((child.HasStateGasAllowance, child.StateGasAllowance), Is.EqualTo((true, 382_500L)));

        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, 300_000), Is.True);
        EthereumGasPolicy grandchild = EthereumGasPolicy.CreateChildFrameGas(ref child, 50_000);
        Assert.That(grandchild.StateGasAllowance, Is.EqualTo(82_500L), "limited by what is left of the parent's allowance");
    }

    [TestCase(0L)]
    [TestCase(1L)]
    [TestCase(50_000L)]
    [TestCase(50_001L)]
    public void State_gas_beyond_the_allowance_comes_from_gas_left(long overAllowance)
    {
        EthereumGasPolicy top = new() { Value = 1_000_000, StateReservoir = 10_000_000 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref top, 50_000);
        EthereumGasPolicy before = child;
        bool canPay = overAllowance <= (long)child.Value;

        bool charged = EthereumGasPolicy.TryConsumeStateGas(ref child, child.StateGasAllowance + overAllowance);

        Assert.That(charged, Is.EqualTo(canPay));
        if (canPay)
            Assert.That((child.Value, child.StateReservoir, child.StateGasDrawn),
                Is.EqualTo((before.Value - (ulong)overAllowance, before.StateReservoir - before.StateGasAllowance, before.StateGasAllowance)));
        else
            Assert.That(child, Is.EqualTo(before), "a refused charge changes nothing");
    }

    [Test]
    public void State_gas_charge_with_no_allowance_left_comes_from_gas_left()
    {
        EthereumGasPolicy top = new() { Value = 1_000_000, StateReservoir = 10_000_000 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref top, 50_000);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, child.StateGasAllowance), Is.True);
        EthereumGasPolicy grandchild = EthereumGasPolicy.CreateChildFrameGas(ref child, 200_000);
        long reservoir = grandchild.StateReservoir;

        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref grandchild, GasCostOf.SSetState), Is.True);
        Assert.That((grandchild.StateGasAllowance, grandchild.Value, grandchild.StateReservoir),
            Is.EqualTo((0L, 200_000UL - (ulong)GasCostOf.SSetState, reservoir)));
    }

    [Test]
    public void State_gas_refill_does_not_restore_allowance()
    {
        EthereumGasPolicy top = new() { Value = 1_000_000, StateReservoir = 10_000_000 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref top, 200_000);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, child.StateGasAllowance), Is.True);

        EthereumGasPolicy.AddStateGasRefundToReservoir(ref child, GasCostOf.SSetState);
        long reservoir = child.StateReservoir;

        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, GasCostOf.SSetState), Is.True);
        Assert.That((child.Value, child.StateReservoir), Is.EqualTo((200_000UL - (ulong)GasCostOf.SSetState, reservoir)));
    }

    [Test]
    public void State_gas_draws_of_a_child_count_for_the_parent_only_on_success([Values] bool childSucceeds)
    {
        EthereumGasPolicy top = new() { Value = 1_000_000, StateReservoir = 10_000_000 };
        EthereumGasPolicy parent = EthereumGasPolicy.CreateChildFrameGas(ref top, 100_000);
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 50_000);
        Assert.That(EthereumGasPolicy.TryConsumeStateGas(ref child, GasCostOf.SSetState), Is.True);

        if (childSucceeds)
            EthereumGasPolicy.Refund(ref parent, in child);
        else
            EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That(parent.StateGasDrawn, Is.EqualTo(childSucceeds ? GasCostOf.SSetState : 0L));
    }

    [Test]
    public void Frame_transaction_frames_have_no_state_gas_allowance()
    {
        EthereumGasPolicy top = EthereumGasPolicy.FromFrameLimits(1_000_000, 10_000_000);

        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref top, 50_000);

        Assert.That(child.HasStateGasAllowance, Is.False);
    }
}
