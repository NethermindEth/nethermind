// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EVM-opcode-level coverage for EIP-2780 repricing. Each test differentials two runs that differ
/// only in the operation under test, so shared intrinsic/recipient costs cancel and isolate the delta.
/// </summary>
public class Eip2780VmTests : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider { get; } =
        new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true });

    private ulong GasSpent(byte[] code)
    {
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success), result.Error);
        return result.GasSpent;
    }

    // Gas charged at the given opcode's step, per the Geth-style trace.
    private ulong OpCost(string opcode, byte[] code)
    {
        foreach (global::Nethermind.Blockchain.Tracing.GethStyle.GethTxTraceEntry e in ExecuteAndTrace(code).Entries)
        {
            if (e.Opcode == opcode) return e.GasCost;
        }
        return ulong.MaxValue;
    }

    [Test]
    public void Cold_account_access_via_balance_is_two_tier()
    {
        Address codeless = TestItem.AddressC;       // exists, no code -> COLD_ACCOUNT_COST_NOCODE
        Address withCode = TestItem.AddressF;        // has code -> COLD_ACCOUNT_COST_CODE
        TestState.CreateAccount(codeless, 1.Ether);
        TestState.CreateAccount(withCode, 1.Ether);
        TestState.InsertCode(withCode, Prepare.EvmCode.Op(Instruction.STOP).Done, Spec);

        ulong codelessCost = OpCost("BALANCE", Prepare.EvmCode.PushData(codeless).Op(Instruction.BALANCE).STOP().Done);
        ulong withCodeCost = OpCost("BALANCE", Prepare.EvmCode.PushData(withCode).Op(Instruction.BALANCE).STOP().Done);

        Assert.That((codelessCost, withCodeCost), Is.EqualTo((GasCostOf.ColdAccountAccessNoCodeEip2780, GasCostOf.ColdAccountAccess)));
    }

    [Test]
    public void Call_value_cost_new_account_tier_is_24000_above_existing_tier()
    {
        Address existing = TestItem.AddressC;        // exists -> CallValueExistingEip2780 (3756)
        Address newAccount = TestItem.AddressF;       // dead   -> CallValueNewAccountEip2780 (27756)
        TestState.CreateAccount(existing, 1.Ether);

        ulong existingGas = GasSpent(Prepare.EvmCode.CallWithValue(existing, 50000, 1).STOP().Done);
        ulong newAccountGas = GasSpent(Prepare.EvmCode.CallWithValue(newAccount, 50000, 1).STOP().Done);

        Assert.That(newAccountGas - existingGas, Is.EqualTo(GasCostOf.CallValueNewAccountEip2780 - GasCostOf.CallValueExistingEip2780));
    }

    [Test]
    public void Callcode_with_value_is_charged_self_call_tier()
    {
        // CALLCODE keeps caller == target (the executing account), so any value transfer is a
        // self-call priced at a single STATE_UPDATE (1000) instead of the legacy CallValue (9000).
        Address codeSource = TestItem.AddressC;
        TestState.CreateAccount(codeSource, 1.Ether);
        TestState.InsertCode(codeSource, Prepare.EvmCode.Op(Instruction.STOP).Done, Spec);

        ulong noValueOp = OpCost("CALLCODE", Prepare.EvmCode.CallCode(codeSource, 50000, 0).STOP().Done);
        ulong withValueOp = OpCost("CALLCODE", Prepare.EvmCode.CallCode(codeSource, 50000, 1).STOP().Done);

        Assert.That(withValueOp - noValueOp, Is.EqualTo(GasCostOf.CallValueSelfEip2780));
    }

    [Test]
    public void Delegated_recipient_charges_delegation_target_cold_touch_once()
    {
        // A delegated recipient pays COLD_ACCOUNT_COST_CODE for itself and its delegation target. The EVM
        // only warms (not charges) the target at the top level, so the total adds exactly one cold-code touch.
        Address target = TestItem.AddressC;
        TestState.CreateAccount(target, 1.Ether);
        TestState.InsertCode(target, Prepare.EvmCode.Op(Instruction.STOP).Done, Spec);
        byte[] delegated = [.. Eip7702Constants.DelegationHeader, .. target.Bytes];

        ulong delegatedGas = GasSpent(delegated);
        ulong plainContractGas = GasSpent(Prepare.EvmCode.Op(Instruction.STOP).Done);

        Assert.That(delegatedGas - plainContractGas, Is.EqualTo(GasCostOf.ColdAccountAccess));
    }
}
