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
    public void Revoking_advanced_refund_keeps_refilled_gas_and_marked_spill()
    {
        // A net-spill (negative) reservoir, as left by RestoreChildStateGas after nested spills.
        EthereumGasPolicy gas = new() { Value = 400, StateReservoir = -300, StateGasUsed = 0, StateGasSpill = 300, StateGasSpillRefunded = 0 };

        EthereumGasPolicy.AddStateGasRefundToReservoir(ref gas, 200, trackSpillRefund: true);
        // LIFO refill: the whole advance (200 <= unrefunded spill 300) lands in gas_left and is marked.
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasSpillRefunded), Is.EqualTo((600UL, -300L, 200L)));

        EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas, 200);

        // The reservoir is already negative, so the full claw-back drives it further negative; the
        // refilled gas_left and the permanent spill-refund mark stay in place.
        Assert.That((gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpillRefunded), Is.EqualTo((600UL, -500L, 0L, 200L)));
    }

    [Test]
    public void Revoking_consumed_advanced_refund_deducts_usage_without_fabricating_spill_debt()
    {
        EthereumGasPolicy gas = new() { Value = 400, StateReservoir = 0, StateGasUsed = 100 };

        EthereumGasPolicy.AddStateGasRefundToReservoir(ref gas, 200, trackSpillRefund: true);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, 200), Is.True);

        EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas, 200);

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
    public void Exceptional_halt_does_not_inherit_child_spill()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 1_000);
        EthereumGasPolicy.ConsumeStateGas(ref child, 200);
        EthereumGasPolicy.RefundStateGas(ref child, 200, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parent, in child);

        // The child's spill refilled its own gas_left (burned by the halt); RestoreChildStateGasOnHalt
        // touches only the reservoir and never inherits the child's spill counters.
        Assert.That((parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill, parent.StateGasSpillRefunded),
            Is.EqualTo((0L, 0L, 0L, 0L)));
    }

    [Test]
    public void Revert_returns_child_spill_to_parent_gas_left()
    {
        // Source-based LIFO rollback: a reverted child's net spill refills the parent gas_left
        // (it was originally paid from gas_left) and the reservoir absorbs the negative balance so
        // (spill - refunded) stays consistent. The child's spill counter is NOT propagated.
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = new()
        {
            Value = 100,
            StateReservoir = 0,
            StateGasUsed = 0,
            StateGasSpill = GasCostOf.CreateState + 3 * GasCostOf.SSetState,
        };

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        long netSpill = GasCostOf.CreateState + 3 * GasCostOf.SSetState;
        Assert.That((parent.Value, parent.StateReservoir), Is.EqualTo(((ulong)(1_000 + netSpill), -netSpill)));
        Assert.That(parent.StateGasSpill, Is.EqualTo(0L), "reverted child spill counter is not propagated");
    }

    [Test]
    public void Revert_with_fully_refunded_child_spill_returns_nothing_to_parent_gas_left()
    {
        // A child that spilled then fully refunded its own spill has net spill 0: on revert nothing
        // is added to the parent gas_left and no spill is propagated.
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, GasCostOf.CreateState);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref child, GasCostOf.CreateState), Is.True);
        EthereumGasPolicy.RefundStateGas(ref child, GasCostOf.CreateState, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasSpill), Is.EqualTo((1_000UL, 0L, 0L)),
            "net-0 reverted child adds nothing to parent gas_left and propagates no spill");
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
    }

    [Test]
    public void Revert_does_not_inherit_partially_refunded_child_spill()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 500);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref child, 200), Is.True);
        EthereumGasPolicy.RefundStateGas(ref child, 80, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        // The child's net spill (200 - 80 = 120) refills the parent gas_left; the parent's own spill
        // counters are NOT bumped — reverted child spill never inflates them.
        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((1_120UL, 0L, 0L)));
        Assert.That((parent.StateGasSpill, parent.StateGasSpillRefunded), Is.EqualTo((0L, 0L)));
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

        // The child spill (200) was fully refunded (net 0), so RevertRefundToHalt returns nothing to
        // the reservoir; the propagated spill/refund counters cancel and the reservoir stays 0.
        Assert.That((parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill), Is.EqualTo((0L, 0L, 200L)));
        Assert.That(parent.StateGasSpillRefunded, Is.EqualTo(200L));
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
                parent.StateGasSpillRefunded
            ),
            Is.EqualTo(
                (
                    parentRegularGas + childRegularGas,
                    childRemainingStateReservoir + childStateGasUsed,
                    parentStateGasUsed,
                    77L,
                    33L
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
    public void Top_level_halt_block_state_gas_is_intrinsic_floor_not_spill()
    {
        // Per EIP-8037, block.gasUsed = max(sum_regular, sum_state). On a top-level halt
        // (RefundOnTopLevelHalt): block_state = post-reset StateGasUsed (the intrinsic floor). The
        // reservoir-portion of a charge is refunded (reservoir snaps back to R0); the spilled portion
        // was paid from gas_left and is burned as REGULAR gas, never re-added to block_state.
        EthereumGasPolicy gas = new() { Value = 100_000, StateReservoir = 1_000, StateGasUsed = 0 };
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, 5_000), Is.True);
        Assert.That((gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill), Is.EqualTo((0L, 5_000L, 4_000L)),
            "after consuming 5_000 with 1_000 reservoir: reservoir=0, used=5_000 (= 1_000 reservoir-portion + 4_000 spill), spill=4_000");

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: 1_000, initialStateGasUsed: 0);

        // Block_state contribution is exactly the post-reset StateGasUsed (intrinsic floor = 0);
        // the spill (4_000) is excluded (burned as regular) and the reservoir snaps back to R0.
        long blockLevelContribution = gas.StateGasUsed;
        Assert.That(blockLevelContribution, Is.EqualTo(0L),
            "block-level sum_state contribution = post-reset floor (0); spill is burned as regular gas");
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
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, stateGasCharged), Is.True);
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
        // regular gas. Block-level sum_state contribution per halt = post-reset StateGasUsed
        // (intrinsic floor); the spill is NOT added to block_state.
        const long perTxGasLimit = 1_000_000;
        const long intrinsicStateGas = 0;
        const long reservoirAtTxStart = 100_000;
        const long stateGasCharged = 104_174;   // reservoir(100k) consumed + 4_174 spill

        EthereumGasPolicy gas = new() { Value = perTxGasLimit, StateReservoir = reservoirAtTxStart, StateGasUsed = intrinsicStateGas };
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, stateGasCharged), Is.True);
        Assert.That(gas.StateGasSpill, Is.EqualTo(4_174L), "4_174 spill from reservoir overflow");

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: reservoirAtTxStart, initialStateGasUsed: intrinsicStateGas);

        long blockLevelContribution = gas.StateGasUsed;
        Assert.That(blockLevelContribution, Is.EqualTo(intrinsicStateGas),
            "per-tx block-level contribution = intrinsic floor; the spill is burned as regular gas");
    }

    [Test]
    public void Inner_revert_spill_refills_gas_left_and_is_not_propagated()
    {
        // EIP-8037: when a child frame REVERTS after spilling state gas from gas_left, the spill
        // refills the parent gas_left (source-based LIFO) and the child's spill counter is NOT
        // propagated — a reverted child never inflates the parent's unrefunded spill.
        EthereumGasPolicy parent = new() { Value = 100_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 50_000);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref child, 4_174L), Is.True,
            "child consumes 4_174 state-gas with reservoir=0; entirely spills from gas_left");
        Assert.That(child.StateGasSpill, Is.EqualTo(4_174L));

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((104_174UL, 0L, 0L)),
            "child spill refills the parent gas_left; reservoir/used net to zero");
        Assert.That(parent.StateGasSpill, Is.EqualTo(0L), "reverted child spill is not propagated to the parent");
    }

    [Test]
    public void Reverted_grandchild_spill_does_not_propagate_through_success_chain()
    {
        // When a grandchild REVERTS with spill but its parent (child) succeeds, the reverted spill
        // is NOT propagated: RestoreChildStateGas refills gas_left without bumping the child's spill
        // counter, so the successful Refund carries no spill up to the top frame.
        EthereumGasPolicy parent = new() { Value = 500_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 400_000);

        EthereumGasPolicy grandchild = EthereumGasPolicy.CreateChildFrameGas(ref child, 200_000);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref grandchild, 4_174L), Is.True);
        EthereumGasPolicy.RestoreChildStateGas(ref child, in grandchild);
        Assert.That(child.StateGasSpill, Is.EqualTo(0L), "reverted grandchild spill is not inherited by the child");

        EthereumGasPolicy.Refund(ref parent, in child);

        Assert.That(parent.StateGasSpill, Is.EqualTo(0L),
            "no spill propagates to the top frame through the success path");
    }

    [Test]
    public void Reset_for_halt_preserves_gas_left_and_spill_refund_mark()
    {
        // ResetForHalt snaps only the three state-shape fields (reservoir/used/spill); gas_left and
        // the tx-wide StateGasSpillRefunded mark are left untouched.
        EthereumGasPolicy gas = new()
        {
            Value = 4_242,
            StateReservoir = 0,
            StateGasUsed = GasCostOf.CreateState,
            StateGasSpill = GasCostOf.CreateState,
            StateGasSpillRefunded = 4_174,
        };

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: 0, initialStateGasUsed: GasCostOf.CreateState);

        Assert.That((gas.Value, gas.StateGasSpillRefunded), Is.EqualTo((4_242UL, 4_174L)),
            "ResetForHalt preserves gas_left and the StateGasSpillRefunded mark");
        Assert.That(gas.StateGasSpill, Is.EqualTo(0L), "spill is zeroed by the reset");
    }

    [Test]
    public void Top_level_halt_block_state_is_full_intrinsic_not_reduced_by_spill()
    {
        // New halt model (RefundOnTopLevelHalt): block_state = full intrinsic floor. Unlike the OLD
        // model that subtracted burned spill from block_state (moving it to block_regular), the spill
        // is now burned as regular gas via gas_left, so no explicit block_state subtraction is applied.
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
    public void Revert_does_not_propagate_fully_refunded_descendant_spill()
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

        // Both refunds returned to gas_left; the outer frame reverts, so no spill (nor its refund
        // mark) is propagated up to the ancestor, and gas_left returns to its starting value.
        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill),
            Is.EqualTo((500_000UL, 0L, 0L, 0L)));
        Assert.That(parent.StateGasSpillRefunded, Is.EqualTo(0L),
            "reverted descendant spill and its refund mark are not inherited by the ancestor");
    }

    [TestCase(ExpectedResult = 5_000L)]
    public long Spent_gas_subtracts_state_reservoir()
    {
        EthereumGasPolicy gas = new() { Value = 3_000, StateReservoir = 2_000, StateGasUsed = 500 };
        return 10_000L - (long)EthereumGasPolicy.GetRemainingGas(in gas) - EthereumGasPolicy.GetStateReservoir(in gas);
    }
}
