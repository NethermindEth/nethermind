// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
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
public class Eip8038Tests(bool eip8038Enabled) : VirtualMachineTestsBase
{
    private readonly ISpecProvider _specProvider =
        new TestSpecProvider(new OverridableReleaseSpec(Cancun.Instance) { IsEip8038Enabled = eip8038Enabled });

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;
    protected override ISpecProvider SpecProvider => _specProvider;

    // The EXT* target; a third address that stays cold (Sender=A, Recipient=B, Miner=D).
    private static readonly Address Target = TestItem.AddressC;

    private ulong ExtraWarmAccess => eip8038Enabled ? Eip8038Constants.WarmAccess : 0;
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
        TestAllTracerWithOutput tracer = base.CreateTracer();
        tracer.IsTracingAccess = false;
        return tracer;
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
/// EIP-8038 raises the transaction access-list entry costs to match the cold-access costs they pre-warm.
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
