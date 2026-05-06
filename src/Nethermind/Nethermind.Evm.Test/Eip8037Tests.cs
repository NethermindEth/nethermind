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

        EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parent, in child, initialStateReservoir: 500);
        Assert.That((parent.StateReservoir, parent.StateGasUsed), Is.EqualTo((500L, 10L)));
    }

    [Test]
    public void Exceptional_halt_does_not_restore_spilled_state_refund_above_initial_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 1_000);
        EthereumGasPolicy.ConsumeStateGas(ref child, 200);
        EthereumGasPolicy.RefundStateGas(ref child, 200, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parent, in child, initialStateReservoir: 0);

        // Spill must NOT propagate to parent.StateGasSpill or parent.StateGasSpillBurned on
        // exceptional halt: halt burns the child's regular gas (incl. the spilled portion), which
        // is already in the parent's initial regular budget that the top-level halt formula
        // attributes to the regular dimension. Adding it via either StateGasSpill (subtraction in
        // non-halt formula) or StateGasSpillBurned (reattribution in halt formula) would
        // double-count (regression caught by bal-devnet-6 block 423).
        Assert.That((parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill), Is.EqualTo((0L, 0L, 0L)));
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L),
            "child halt does NOT propagate live spill into the cumulative burn counter");
        Assert.That(parent.StateGasSpillReclassified, Is.EqualTo(0L),
            "child halt does NOT reclassify live spill for a containing frame that later succeeds");
    }

    [Test]
    public void Revert_with_state_refund_reclassifies_unrefunded_spill()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = new()
        {
            Value = 100,
            StateReservoir = 0,
            StateGasUsed = 0,
            StateGasSpill = GasCostOf.CreateState + 3 * GasCostOf.SSetState,
        };

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, initialStateReservoir: 0, childStateRefund: GasCostOf.CreateState);

        Assert.That(parent.StateGasSpillReclassified, Is.EqualTo(3 * GasCostOf.SSetState),
            "bal-devnet-6 block 1788: the failed CREATE state refund stays excluded, but the three SSTORE spills remain block-regular gas");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(3 * GasCostOf.SSetState));
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

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, initialStateReservoir: 0, childStateRefund: GasCostOf.CreateState);

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

        Assert.That((gas.StateGasUsed, gas.StateReservoir, gas.StateGasSpill, gas.StateGasSpillRefunded),
            Is.EqualTo((120L, 80L, 200L, 80L)));
    }

    [Test]
    public void Refund_state_gas_from_exceptional_halt_does_not_mark_spilled_refund()
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
        Assert.That((parent.StateGasSpillReclassified, parent.StateGasSpillBurned), Is.EqualTo((0L, 0L)));
    }

    [Test]
    public void Revert_with_partial_spill_refund_reclassifies_only_unrefunded_spill()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 500);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref child, 200), Is.True);
        EthereumGasPolicy.RefundStateGas(ref child, 80, stateGasFloor: 0);

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, initialStateReservoir: 0, childStateRefund: 80);

        Assert.That(parent.StateGasSpillRefunded, Is.EqualTo(80L));
        Assert.That(parent.StateGasSpillReclassified, Is.EqualTo(120L),
            "only the unrefunded spill is moved back to block regular gas");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(120L),
            "only the unrefunded spill contributes to top-level halt reattribution");
    }

    [Test]
    public void Code_deposit_halt_does_not_restore_spilled_state_refund_above_initial_reservoir()
    {
        EthereumGasPolicy parent = new() { Value = 1_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 1_000);
        EthereumGasPolicy.ConsumeStateGas(ref child, 200);
        EthereumGasPolicy.RefundStateGas(ref child, 200, stateGasFloor: 0);

        EthereumGasPolicy.Refund(ref parent, in child);
        EthereumGasPolicy.RevertRefundToHalt(ref parent, in child, initialStateReservoir: 0);

        // Code-deposit-failure leaves parent.StateGasSpill carrying the child's spill so
        // Calculate8037BlockRegularGas (non-halt path) reattributes it from regular to state.
        // Rerouting into StateGasSpillBurned would double-attribute when the parent eventually
        // succeeds at the top level (regression caught by bal-devnet-6 block 423).
        Assert.That((parent.StateReservoir, parent.StateGasUsed, parent.StateGasSpill), Is.EqualTo((0L, 0L, 200L)));
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L),
            "code-deposit halt does NOT reroute live spill into the cumulative burn counter");
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
        // Regression-guard: snapshotting full StateGasUsed (instead of just spill) over-counts
        // block-level sum_state by the reservoir-portion. This was the symptom seen at block 1780
        // of bal-devnet-6 fixtures: 1 SSTORE charged from reservoir (no spill) caused +37,568
        // overcount in block.gasUsed when the full StateGasUsed was used for the snapshot.
        const long reservoirAtTxStart = 100_000;
        const long stateGasCharged = 37_568;    // 1 SSetState, fully covered by reservoir, no spill

        EthereumGasPolicy gas = new() { Value = 1_000_000, StateReservoir = reservoirAtTxStart, StateGasUsed = 0 };
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref gas, stateGasCharged), Is.True);
        Assert.That(gas.StateGasSpill, Is.EqualTo(0L), "reservoir covers full charge; no spill");
        Assert.That(gas.StateGasUsed, Is.EqualTo(stateGasCharged), "full charge recorded in StateGasUsed");

        long wrongSnapshot = gas.StateGasUsed;     // = 37_568 — what the buggy fix would use
        long correctSnapshot = gas.StateGasSpill;  // = 0 — what the canonical contribution is

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: reservoirAtTxStart, initialStateGasUsed: 0);

        long blockLevelCorrect = gas.StateGasUsed + correctSnapshot;
        long blockLevelWrong = gas.StateGasUsed + wrongSnapshot;
        Assert.That(blockLevelWrong - blockLevelCorrect, Is.EqualTo(37_568L),
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
    public void Inner_revert_burned_spill_propagates_to_state_gas_spill_burned()
    {
        // EIP-8037: when a child frame REVERTS after spilling state gas from gas_left, that
        // spill is regular gas burned to pay for state work that is now reverted. The child's
        // remaining regular gas is returned, but the spilled portion is gone. For block-level
        // accounting, this burned spill should be reattributed from the state dimension to the
        // regular dimension at the top-level halt formula.
        EthereumGasPolicy parent = new() { Value = 100_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 50_000);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref child, 4_174L), Is.True,
            "child consumes 4_174 state-gas with reservoir=0; entirely spills from gas_left");
        Assert.That(child.StateGasSpill, Is.EqualTo(4_174L));

        EthereumGasPolicy.RestoreChildStateGas(ref parent, in child, initialStateReservoir: 0, childStateRefund: 0);

        Assert.That(parent.StateGasSpill, Is.EqualTo(4_174L), "live spill propagates to parent for non-halt accounting");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(4_174L),
            "inner-revert burned-spill is also recorded in tx-cumulative counter for halt accounting");
    }

    [Test]
    public void State_gas_spill_burned_propagates_through_success_chain()
    {
        // Cumulative invariant: when a grandchild reverts with spill but its child succeeds,
        // the burned-spill counter must propagate up through the success chain so the top-level
        // halt formula sees it.
        EthereumGasPolicy parent = new() { Value = 500_000, StateReservoir = 0, StateGasUsed = 0 };
        EthereumGasPolicy child = EthereumGasPolicy.CreateChildFrameGas(ref parent, 400_000);

        EthereumGasPolicy grandchild = EthereumGasPolicy.CreateChildFrameGas(ref child, 200_000);
        Assert.That(EthereumGasPolicy.ConsumeStateGas(ref grandchild, 4_174L), Is.True);
        EthereumGasPolicy.RestoreChildStateGas(ref child, in grandchild, initialStateReservoir: 0, childStateRefund: 0);
        Assert.That(child.StateGasSpillBurned, Is.EqualTo(4_174L));

        EthereumGasPolicy.Refund(ref parent, in child);

        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(4_174L),
            "Refund propagates child.StateGasSpillBurned cumulatively through the success path");
    }

    [Test]
    public void Reset_for_halt_preserves_state_gas_spill_burned()
    {
        // ResetForHalt zeros live state-gas tracking (StateGasUsed/Reservoir/Spill) but MUST NOT
        // reset StateGasSpillBurned: that counter is tx-wide cumulative and is consumed by the
        // top-level halt formula AFTER ResetForHalt to reattribute the burn.
        EthereumGasPolicy gas = new()
        {
            Value = 0,
            StateReservoir = 0,
            StateGasUsed = 131_488,
            StateGasSpill = 0,
            StateGasSpillBurned = 4_174,
        };

        EthereumGasPolicy.ResetForHalt(ref gas, initialStateReservoir: 0, initialStateGasUsed: 131_488);

        Assert.That(gas.StateGasSpillBurned, Is.EqualTo(4_174L),
            "StateGasSpillBurned survives ResetForHalt so the halt formula can reattribute the burned spill");
    }

    [Test]
    public void Top_level_halt_block_regular_dimension_includes_burned_spill()
    {
        // Regression for bal-devnet-6 block 1788 (and EIP-8037 in general): a top-level halt
        // tx with N inner-frame reverts that each spilled S of state gas from gas_left should
        // contribute (initialRegular + N*S) to block_regular and (intrinsicState - N*S) to
        // block_state. The burned spill belongs in the regular dimension because it was paid
        // from gas_left, not from the reservoir.
        const long txGasLimit = 16_000_000;
        const long intrinsicStateGas = 131_488;   // EIP-8037 CREATE state cost
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
        Assert.That(parent.StateGasSpillReclassified, Is.EqualTo(0L),
            "ancestor refunds consumed both spills, so neither is reclassified to block regular gas");
        Assert.That(parent.StateGasSpillBurned, Is.EqualTo(0L),
            "refunded descendant spill must not be added by the top-level halt formula");
    }

    [TestCase(ExpectedResult = 5_000L)]
    public long Spent_gas_subtracts_state_reservoir()
    {
        EthereumGasPolicy gas = new() { Value = 3_000, StateReservoir = 2_000, StateGasUsed = 500 };
        return 10_000L - EthereumGasPolicy.GetRemainingGas(in gas) - EthereumGasPolicy.GetStateReservoir(in gas);
    }
}
