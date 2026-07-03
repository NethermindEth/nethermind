// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
/// Tests for EIP-8151: ecRecover charges an EIP-2929 account access on the recovered address
/// (warming it) and returns zero when the address has permanently disabled its ECDSA authority
/// (EIP-7851 <c>0xef0101</c> designator).
/// </summary>
[TestFixture]
public class Eip8151Tests : VirtualMachineTestsBase
{
    // Known-good vector shared with ECRecoverPrecompileTests.
    private const string InputHex =
        "38d18acb67d25c8bb9942764b62f18e17054f66a817bd4295423adf9ed98873e" +
        "000000000000000000000000000000000000000000000000000000000000001b" +
        "38d18acb67d25c8bb9942764b62f18e17054f66a817bd4295423adf9ed98873e" +
        "789d1dd423d25f0772d2748d60f7e4b81bb14d086eba8e8e8efb6dcff8a4ae02";

    private static readonly Address RecoveredAddress = new("0xceaccac640adf55b2028469bd36ba501f28b699d");

    // Measured region: 6 PUSH1/PUSH3 (18) + PUSH0 (2) + CALL (100 warm precompile + 3000
    // ecRecover base) + POP (2) + GAS (2).
    private const ulong MeasuredOverhead = 6 * GasCostOf.VeryLow + GasCostOf.Base + GasCostOf.WarmStateRead + 3000 + GasCostOf.Base + GasCostOf.Base;

    protected override ISpecProvider SpecProvider { get; } = new TestSpecProvider(
        new OverridableReleaseSpec(Prague.Instance) { IsEip7851Enabled = true, IsEip8151Enabled = true });

    // Access tracing pre-warms every touched account, which would hide the cold/warm
    // distinction these tests assert.
    protected override TestAllTracerWithOutput CreateTracer() => new() { IsTracingAccess = false };

    /// <summary>
    /// Writes the 128-byte input at memory 0x20, then runs <paramref name="calls"/> measured
    /// ecRecover calls. The output word of the last call lands at return offset 0; the i-th
    /// call's measured gas delta lands at return offset 0x20 + 32i.
    /// </summary>
    private static byte[] BuildMeasuredEcRecoverCode(int calls)
    {
        byte[] input = Bytes.FromHexString(InputHex);
        Prepare code = Prepare.EvmCode
            .PushData(input.AsSpan(0, 32).ToArray()).PushData(0x20).Op(Instruction.MSTORE)
            .PushData(input.AsSpan(32, 32).ToArray()).PushData(0x40).Op(Instruction.MSTORE)
            .PushData(input.AsSpan(64, 32).ToArray()).PushData(0x60).Op(Instruction.MSTORE)
            .PushData(input.AsSpan(96, 32).ToArray()).PushData(0x80).Op(Instruction.MSTORE)
            // Pre-touch the memory used for deltas so the measured region has no expansion cost.
            .PushData(1).PushData(0xc0 + 32 * (calls - 1)).Op(Instruction.MSTORE);

        for (int i = 0; i < calls; i++)
        {
            code = code
                .Op(Instruction.GAS)            // snapshot below the call arguments
                .PushData(0x20).PushData(0xa0)  // retLength, retOffset
                .PushData(0x80).PushData(0x20)  // argsLength, argsOffset
                .Op(Instruction.PUSH0)          // value (PUSH0 for a deterministic width)
                .PushData(1)                    // ecRecover
                .PushData(200000)               // forwarded gas (PUSH3)
                .Op(Instruction.CALL)
                .Op(Instruction.POP)
                .Op(Instruction.GAS)
                .Op(Instruction.SWAP1)
                .Op(Instruction.SUB)
                .PushData(0xc0 + 32 * i).Op(Instruction.MSTORE);
        }

        return code
            .PushData(0x20 + 32 * calls).PushData(0xa0).Op(Instruction.RETURN)
            .Done;
    }

    private static UInt256 Delta(byte[] returnValue, int call) =>
        new(returnValue.AsSpan(0x20 + 32 * call, 32), isBigEndian: true);

    [Test]
    public void Successful_recovery_charges_cold_account_access()
    {
        TestAllTracerWithOutput result = Execute(BuildMeasuredEcRecoverCode(calls: 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReturnValue.AsSpan(12, 20).ToArray(), Is.EqualTo(RecoveredAddress.Bytes.ToArray()));
            Assert.That(Delta(result.ReturnValue, 0), Is.EqualTo((UInt256)(MeasuredOverhead + GasCostOf.ColdAccountAccess)));
        }
    }

    [Test]
    public void Second_recovery_of_same_address_is_warm()
    {
        TestAllTracerWithOutput result = Execute(BuildMeasuredEcRecoverCode(calls: 2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Delta(result.ReturnValue, 0), Is.EqualTo((UInt256)(MeasuredOverhead + GasCostOf.ColdAccountAccess)));
            Assert.That(Delta(result.ReturnValue, 1), Is.EqualTo((UInt256)(MeasuredOverhead + GasCostOf.WarmStateRead)));
        }
    }

    [Test]
    public void Ecdsa_disabled_recovered_address_returns_zero()
    {
        byte[] designator = [.. Eip7851Constants.DelegationHeader, .. TestItem.AddressE.Bytes];
        TestState.CreateAccount(RecoveredAddress, 0);
        TestState.InsertCode(RecoveredAddress, ValueKeccak.Compute(designator), designator, Spec);
        TestState.Commit(Spec);

        TestAllTracerWithOutput result = Execute(BuildMeasuredEcRecoverCode(calls: 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReturnValue.AsSpan(0, 32).ToArray(), Is.EqualTo(new byte[32]), "must return zero for a deactivated authority");
            Assert.That(Delta(result.ReturnValue, 0), Is.EqualTo((UInt256)(MeasuredOverhead + GasCostOf.ColdAccountAccess)));
        }
    }

    [Test]
    public void Non_disabled_delegated_recovered_address_is_returned()
    {
        byte[] designator = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressE.Bytes];
        TestState.CreateAccount(RecoveredAddress, 0);
        TestState.InsertCode(RecoveredAddress, ValueKeccak.Compute(designator), designator, Spec);
        TestState.Commit(Spec);

        TestAllTracerWithOutput result = Execute(BuildMeasuredEcRecoverCode(calls: 1));

        Assert.That(result.ReturnValue.AsSpan(12, 20).ToArray(), Is.EqualTo(RecoveredAddress.Bytes.ToArray()),
            "a live 0xef0100 delegation must not be treated as deactivated");
    }
}
