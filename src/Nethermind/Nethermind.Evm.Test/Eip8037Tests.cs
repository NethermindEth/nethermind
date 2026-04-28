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
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private static IEnumerable<TestCaseData> ConstantsTestCases()
    {
        yield return new TestCaseData(GasCostOf.CostPerStateByte).Returns(1174L).SetName("CostPerStateByte");
        yield return new TestCaseData(GasCostOf.SSetState).Returns(37568L).SetName("SSetState");
        yield return new TestCaseData(GasCostOf.CreateState).Returns(131488L).SetName("CreateState");
        yield return new TestCaseData(GasCostOf.NewAccountState).Returns(131488L).SetName("NewAccountState");
        yield return new TestCaseData(GasCostOf.PerAuthBaseState).Returns(27002L).SetName("PerAuthBaseState");
    }

    [TestCaseSource(nameof(ConstantsTestCases))]
    public long Constants_are_calculated_correctly(long actual) => actual;

    [TestCase(0L)]
    [TestCase(1_000_000L)]
    [TestCase(30_000_000L)]
    [TestCase(100_000_000L)]
    [TestCase(200_000_000L)]
    public void Cost_per_state_byte_is_static(long blockGasLimit) =>
        Assert.That(GasCostOf.CalculateCostPerStateByte(blockGasLimit), Is.EqualTo(GasCostOf.CostPerStateByte));

    [TestCase(1, ExpectedResult = 6L)]
    [TestCase(32, ExpectedResult = 6L)]
    [TestCase(33, ExpectedResult = 12L)]
    public long Code_deposit_regular_cost(int codeLength)
    {
        CodeDepositHandler.CalculateCost(Amsterdam.Instance, codeLength, out long regularCost, out _);
        return regularCost;
    }

    [TestCase(1, ExpectedResult = 1174L)]
    [TestCase(32, ExpectedResult = 37568L)]
    [TestCase(33, ExpectedResult = 38742L)]
    public long Code_deposit_state_cost(int codeLength)
    {
        CodeDepositHandler.CalculateCost(Amsterdam.Instance, codeLength, out _, out long stateCost);
        return stateCost;
    }

    [Test]
    public void Gas_policy_exposes_dynamic_state_costs()
    {
        long costPerStateByte = GasCostOf.CalculateCostPerStateByte(30_000_000);
        EthereumGasPolicy gas = new() { CostPerStateByte = costPerStateByte };

        Assert.That(
            (
                EthereumGasPolicy.GetCostPerStateByte(in gas),
                EthereumGasPolicy.GetStorageSetStateCost(in gas),
                EthereumGasPolicy.GetCreateStateCost(in gas),
                EthereumGasPolicy.GetNewAccountStateCost(in gas),
                EthereumGasPolicy.GetPerAuthBaseStateCost(in gas)
            ),
            Is.EqualTo(
                (
                    costPerStateByte,
                    GasCostOf.CalculateSSetState(costPerStateByte),
                    GasCostOf.CalculateCreateState(costPerStateByte),
                    GasCostOf.CalculateNewAccountState(costPerStateByte),
                    GasCostOf.CalculatePerAuthBaseState(costPerStateByte)
                )));
    }

    [Test]
    public void Generic_code_deposit_cost_uses_policy_state_pricing()
    {
        long costPerStateByte = GasCostOf.CalculateCostPerStateByte(30_000_000);
        EthereumGasPolicy gas = new() { CostPerStateByte = costPerStateByte };

        bool success = CodeDepositHandler.CalculateCost(Amsterdam.Instance, 33, in gas, out long regularCost, out long stateCost);

        Assert.That((success, regularCost, stateCost),
            Is.EqualTo((true, 12L, GasCostOf.CalculateCodeDepositState(costPerStateByte, 33))));
    }

    [Test]
    public void Intrinsic_gas_carries_dynamic_cost_per_state_byte()
    {
        Transaction tx = Build.A.Transaction.SignedAndResolved()
            .WithAuthorizationCode(new AuthorizationTuple(1, TestItem.AddressF, 0, 0, UInt256.One, UInt256.One))
            .TestObject;

        IntrinsicGas<EthereumGasPolicy> intrinsicGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, Amsterdam.Instance, 30_000_000);
        long costPerStateByte = GasCostOf.CalculateCostPerStateByte(30_000_000);

        Assert.That(EthereumGasPolicy.GetCostPerStateByte(intrinsicGas.Standard), Is.EqualTo(costPerStateByte));
        Assert.That(intrinsicGas.Standard.StateReservoir,
            Is.EqualTo(GasCostOf.CalculateNewAccountState(costPerStateByte) + GasCostOf.CalculatePerAuthBaseState(costPerStateByte)));
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
        long accessListBaseCost = GasCostOf.AccessAccountListEntry + 3 * GasCostOf.AccessStorageListEntry;
        long accessListFloorTokens = (20L + 3 * 32L) * Amsterdam.Instance.GasCosts.TxDataNonZeroMultiplier;
        long accessListFloorCost = accessListFloorTokens * Amsterdam.Instance.GasCosts.TotalCostFloorPerToken;
        long expectedRegular = GasCostOf.Transaction + accessListBaseCost + accessListFloorCost;
        long expectedFloorGas = GasCostOf.Transaction + accessListFloorCost;

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
        long expectedRegular = GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + GasCostOf.AccessStorageListEntry;

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
    public void Failed_state_spill_preserves_existing_reservoir()
    {
        EthereumGasPolicy gas = new() { Value = 10, StateReservoir = 50, StateGasUsed = 0, StateGasSpill = 0 };

        bool consumed = EthereumGasPolicy.ConsumeStateGas(ref gas, 70);

        Assert.That((consumed, gas.Value, gas.StateReservoir, gas.StateGasUsed, gas.StateGasSpill),
            Is.EqualTo((false, 10L, 50L, 0L, 0L)));
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
    public void Code_insert_state_refund_is_available_to_later_state_gas()
    {
        const long intrinsicAuthState = GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState;
        EthereumGasPolicy gas = new()
        {
            StateGasUsed = intrinsicAuthState,
        };

        long regularRefund = EthereumGasPolicy.ApplyCodeInsertRefunds(ref gas, 1, Amsterdam.Instance, intrinsicAuthState);
        EthereumGasPolicy.ConsumeStateGas(ref gas, GasCostOf.SSetState);
        EthereumGasPolicy.ConsumeStateGas(ref gas, GasCostOf.SSetState);

        Assert.That(regularRefund, Is.Zero);
        Assert.That((gas.StateReservoir, gas.StateGasUsed),
            Is.EqualTo((GasCostOf.NewAccountState - 2 * GasCostOf.SSetState, intrinsicAuthState + 2 * GasCostOf.SSetState)));
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
            transferValue: UInt256.Zero,
            value: UInt256.Zero,
            inputData: ReadOnlyMemory<byte>.Empty);
        EthereumGasPolicy gas = new()
        {
            Value = 1_000_000,
            StateReservoir = GasCostOf.CreateState,
            CostPerStateByte = GasCostOf.CostPerStateByte,
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

        Assert.That((vmState.Gas.StateGasUsed, vmState.Gas.StateReservoir, vmState.StateGasRefundPending),
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

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, 500, childStateRefund: 0);
        Assert.That((parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((500L, 10L)));
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
        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, 400, childStateRefund: 0);

        Assert.That((parent.Value, parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((900L, 400L, 20L)));
    }

    [Test]
    public void Revert_discards_child_inline_state_refund_inflation()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 400, StateGasUsed = 20 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 600);
        EthereumGasPolicy.ConsumeStateGas(ref child, 150);
        EthereumGasPolicy.RefundStateGas(ref child, 40, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, 400, childStateRefund: 40);

        Assert.That((parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((360L, 20L)));
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
        EthereumGasPolicy.RestoreChildStateGas(ref outer, in inner, initialStateReservoir: 0, childStateRefund: GasCostOf.SSetState);

        EthereumGasPolicy.RefundStateGas(ref outer, GasCostOf.CreateState, stateGasFloor: 0);
        EthereumGasPolicy.RestoreChildStateGas(ref parent, in outer, initialStateReservoir: 0, childStateRefund: GasCostOf.CreateState);

        Assert.That((parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill),
            Is.EqualTo((0L, 0L, GasCostOf.CreateState + GasCostOf.SSetState)));
    }

    [TestCase(ExpectedResult = 5_000L)]
    public long Spent_gas_subtracts_state_reservoir()
    {
        EthereumGasPolicy gas = new() { Value = 3_000, StateReservoir = 2_000, StateGasUsed = 500 };
        return 10_000L - EthereumGasPolicy.GetRemainingGas(in gas) - EthereumGasPolicy.GetStateReservoir(in gas);
    }
}
