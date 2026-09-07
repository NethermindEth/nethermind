// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-8038: State-access gas cost update. With the EIP active, EXTCODESIZE and EXTCODECOPY pay an
/// additional WARM_ACCESS for the extra database read they perform.
/// </summary>
[TestFixture(true)]
[TestFixture(false)]
[TestFixture(true, false, false)]
[TestFixture(false, false, false)]
[TestFixture(true, true, true)]
[TestFixture(false, true, true)]
[TestFixture(true, false, true)]
[TestFixture(false, false, true)]
public class Eip8038Tests(bool eip8038Enabled, bool tracing = true, bool cancelable = false) : VirtualMachineTestsBase
{
    private readonly ISpecProvider _specProvider =
        new TestSpecProvider(new OverridableReleaseSpec(Cancun.Instance) { IsEip8038Enabled = eip8038Enabled });

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;
    protected override ISpecProvider SpecProvider => _specProvider;

    // The EXT* target; a third address that stays cold (Sender=A, Recipient=B, Miner=D).
    private static readonly Address Target = TestItem.AddressC;

    private ulong ExtraWarmAccess => eip8038Enabled ? Eip8038Constants.WarmAccess : 0;
    private ulong ColdStorageAccess => eip8038Enabled ? Eip8038Constants.ColdStorageAccess : GasCostOf.ColdSLoad;
    private ulong ColdAccountAccess => eip8038Enabled ? Eip8038Constants.ColdAccountAccess : GasCostOf.ColdAccountAccess;

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        // Cold-access cost is independent of whether the target has code (EIP-2780 is off here).
        TestState.CreateAccount(Target, 1.Ether);
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);
    }

    protected override TestAllTracerWithOutput CreateTracer()
    {
        TestAllTracerWithOutput tracer = new SpecializationTracer(tracing, cancelable);
        tracer.IsTracingAccess = false;
        return tracer;
    }

    private sealed class SpecializationTracer(bool tracing, bool cancelable) : TestAllTracerWithOutput, ITxTracer
    {
        public override bool IsTracingInstructions => tracing;
        bool ITxTracer.IsCancelable => cancelable;
    }

    [TestCase(Instruction.EXTCODESIZE, 0)]
    [TestCase(Instruction.EXTCODESIZE, -1)]
    [TestCase(Instruction.EXTCODECOPY, 0)]
    [TestCase(Instruction.EXTCODECOPY, -1)]
    public void ExtCode_access_obeys_exact_gas_boundary(Instruction instruction, int gasDelta)
    {
        byte[] code = instruction == Instruction.EXTCODESIZE
            ? Prepare.EvmCode.PushData(Target).Op(instruction).STOP().Done
            : Prepare.EvmCode.PushData(0).PushData(0).PushData(0).PushData(Target).Op(instruction).STOP().Done;
        ulong pushGas = (instruction == Instruction.EXTCODESIZE ? 1UL : 4UL) * GasCostOf.VeryLow;
        ulong gasLimit = (ulong)((long)(GasCostOf.Transaction + pushGas + ColdAccountAccess + ExtraWarmAccess) + gasDelta);

        TestAllTracerWithOutput result = Execute(Activation, gasLimit, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(gasDelta == 0 ? StatusCode.Success : StatusCode.Failure));
            Assert.That(result.GasSpent, Is.EqualTo(gasLimit));
        }
    }

    [TestCase(Instruction.SLOAD, false)]
    [TestCase(Instruction.SLOAD, true)]
    [TestCase(Instruction.SSTORE, false)]
    [TestCase(Instruction.SSTORE, true)]
    public void Storage_access_charges_cold_then_warm(Instruction instruction, bool repeat)
    {
        byte[] operation = instruction == Instruction.SLOAD
            ? Prepare.EvmCode.PushData(0).Op(instruction).Op(Instruction.POP).Done
            : Prepare.EvmCode.PushData(0).PushData(0).Op(instruction).Done;
        byte[] code = repeat ? [.. operation, .. operation] : operation;
        ulong stackCost = instruction == Instruction.SLOAD ? GasCostOf.VeryLow + GasCostOf.Base : 2 * GasCostOf.VeryLow;
        ulong coldCost = ColdStorageAccess + (instruction == Instruction.SSTORE && !eip8038Enabled ? GasCostOf.WarmStateRead : 0);
        ulong expected = GasCostOf.Transaction + stackCost + coldCost
            + (repeat ? stackCost + GasCostOf.WarmStateRead : 0);

        TestAllTracerWithOutput result = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(result.GasSpent, Is.EqualTo(expected));
        }
    }

    [TestCase((byte)0)]
    [TestCase((byte)1)]
    public void Storage_reversal_refunds_first_write(byte originalValue)
    {
        StorageCell cell = new(Recipient, 0);
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(in cell, [originalValue]);
        TestState.Commit(SpecProvider.GenesisSpec);
        byte[] code = Prepare.EvmCode.PushData(2).PushData(0).Op(Instruction.SSTORE)
            .PushData(originalValue).PushData(0).Op(Instruction.SSTORE).Done;
        ulong writeCost = originalValue == 0 ? GasCostOf.SSet
            : eip8038Enabled ? Eip8038Constants.StorageWrite : GasCostOf.SReset - GasCostOf.ColdSLoad;
        ulong refund = eip8038Enabled ? Eip8038Constants.StorageWrite
            : originalValue == 0 ? RefundOf.SSetReversedHotCold : RefundOf.SResetReversedHotCold;
        ulong spent = GasCostOf.Transaction + 4 * GasCostOf.VeryLow + ColdStorageAccess + writeCost + GasCostOf.WarmStateRead;

        TestAllTracerWithOutput result = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(result.Refund, Is.EqualTo(refund));
            Assert.That(result.GasSpent, Is.EqualTo(spent - Math.Min(spent / 5, refund)));
            Assert.That(TestState.Get(in cell).ToArray(), Is.EqualTo(new byte[] { originalValue }));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Selfdestruct_charges_beneficiary_access_and_creation(bool newBeneficiary)
    {
        Address beneficiary = newBeneficiary ? TestItem.AddressE : Target;
        byte[] code = Prepare.EvmCode.SELFDESTRUCT(beneficiary).Done;
        ulong expected = GasCostOf.Transaction + GasCostOf.VeryLow + GasCostOf.SelfDestructEip150 + ColdAccountAccess;
        if (newBeneficiary)
            expected += GasCostOf.NewAccount + (eip8038Enabled ? Eip8038Constants.AccountWrite : 0);

        TestAllTracerWithOutput result = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(result.GasSpent, Is.EqualTo(expected));
            Assert.That(TestState.GetBalance(beneficiary), Is.GreaterThan(UInt256.Zero));
        }
    }

    [Test]
    public void ExtCodeSize_charges_extra_warm_access()
    {
        byte[] code = Prepare.EvmCode
            .PushData(Target)
            .Op(Instruction.EXTCODESIZE)
            .Op(Instruction.POP)
            .STOP()
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        ulong expected = GasCostOf.Transaction
                        + GasCostOf.VeryLow            // PUSH20 target
                        + ColdAccountAccess            // cold EXTCODESIZE access (EIP-8038 repriced when enabled)
                        + ExtraWarmAccess              // EIP-8038 extra access
                        + GasCostOf.Base;              // POP
        AssertGas(result, expected);
    }

    [Test]
    public void ExtCodeCopy_charges_extra_warm_access()
    {
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .PushData(0)
            .PushData(Target)
            .Op(Instruction.EXTCODECOPY)
            .STOP()
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        ulong expected = GasCostOf.Transaction
                        + 4 * GasCostOf.VeryLow        // three PUSH1 0x00 + PUSH20 target
                        + ColdAccountAccess            // cold EXTCODECOPY access (EIP-8038 repriced when enabled)
                        + ExtraWarmAccess;             // EIP-8038 extra access
        AssertGas(result, expected);
    }

    [Test]
    public void ExtCodeSize_charges_extra_warm_access_on_warm_account()
    {
        byte[] code = Prepare.EvmCode
            .PushData(Target).Op(Instruction.EXTCODESIZE).Op(Instruction.POP)
            .PushData(Target).Op(Instruction.EXTCODESIZE).Op(Instruction.POP)
            .STOP()
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        ulong expected = GasCostOf.Transaction
                        + 2 * GasCostOf.VeryLow        // two PUSH20 target
                        + ColdAccountAccess            // cold EXTCODESIZE access (first; EIP-8038 repriced when enabled)
                        + GasCostOf.WarmStateRead      // warm EXTCODESIZE access (second)
                        + 2 * ExtraWarmAccess          // EIP-8038 extra access on both
                        + 2 * GasCostOf.Base;          // two POP
        AssertGas(result, expected);
    }

    [Test]
    public void ExtCodeCopy_charges_extra_warm_access_on_warm_account()
    {
        byte[] code = Prepare.EvmCode
            .PushData(0).PushData(0).PushData(0).PushData(Target).Op(Instruction.EXTCODECOPY)
            .PushData(0).PushData(0).PushData(0).PushData(Target).Op(Instruction.EXTCODECOPY)
            .STOP()
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        ulong expected = GasCostOf.Transaction
                        + 8 * GasCostOf.VeryLow        // two groups of three PUSH1 0x00 + PUSH20 target
                        + ColdAccountAccess            // cold EXTCODECOPY access (first; EIP-8038 repriced when enabled)
                        + GasCostOf.WarmStateRead      // warm EXTCODECOPY access (second)
                        + 2 * ExtraWarmAccess;         // EIP-8038 extra access on both
        AssertGas(result, expected);
    }
}

/// <summary>
/// EIP-8038 raises transaction access-list entry costs while subtracting the warm charge paid on use.
/// </summary>
public class Eip8038IntrinsicGasTests
{
    private static IReleaseSpec Spec(bool eip8038Enabled) =>
        new OverridableReleaseSpec(Cancun.Instance) { IsEip8038Enabled = eip8038Enabled };

    [TestCase(false, 21000 + GasCostOf.AccessAccountListEntry, TestName = "address entry, EIP-8038 off")]
    [TestCase(true, 21000 + Eip8038Constants.AccessListAddressCost, TestName = "address entry, EIP-8038 on")]
    public void Access_list_address_entry_cost(bool eip8038Enabled, ulong expectedStandard)
    {
        AccessList accessList = new AccessList.Builder().AddAddress(TestItem.AddressC).Build();
        Transaction tx = Build.A.Transaction.SignedAndResolved().WithAccessList(accessList).TestObject;

        EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Spec(eip8038Enabled));

        Assert.That(gas.Standard, Is.EqualTo(expectedStandard));
    }

    [TestCase(false, 21000 + GasCostOf.AccessAccountListEntry + GasCostOf.AccessStorageListEntry, TestName = "address + key, EIP-8038 off")]
    [TestCase(true, 21000 + Eip8038Constants.AccessListAddressCost + Eip8038Constants.AccessListStorageKeyCost, TestName = "address + key, EIP-8038 on")]
    public void Access_list_address_and_storage_key_cost(bool eip8038Enabled, ulong expectedStandard)
    {
        AccessList accessList = new AccessList.Builder()
            .AddAddress(TestItem.AddressC)
            .AddStorage((UInt256)1)
            .Build();
        Transaction tx = Build.A.Transaction.SignedAndResolved().WithAccessList(accessList).TestObject;

        EthereumIntrinsicGas gas = IntrinsicGasCalculator.Calculate(tx, Spec(eip8038Enabled));

        Assert.That(gas.Standard, Is.EqualTo(expectedStandard));
    }
}
