// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>EIP-7666: 0x04 stops being the identity precompile and runs <see cref="Eip7666Constants.IdentityCode"/> instead.</summary>
/// <remarks>
/// With EIP-7666 enabled the state holds the code the fork block installs; with it disabled 0x04 is the plain
/// precompile, which is the flag-off control for every case.
/// </remarks>
[TestFixture(true)]
[TestFixture(false)]
public class Eip7666Tests(bool eip7666Enabled) : VirtualMachineTestsBase
{
    private const ulong CallGas = 50_000;
    private const int ReturnCopyOffset = 0x400;

    private static readonly Address Identity = Eip7666Constants.IdentityAddress;

    private readonly ISpecProvider _specProvider =
        new TestSpecProvider(new OverridableReleaseSpec(Bogota.Instance) { IsEip7666Enabled = eip7666Enabled });

    protected override ISpecProvider SpecProvider => _specProvider;

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        if (eip7666Enabled)
        {
            TestState.CreateAccount(Identity, UInt256.Zero);
            TestState.InsertCode(Identity, Eip7666Constants.IdentityCode, Spec);
        }

        TestState.Commit(Spec);
    }

    [Test]
    public void Identity_leaves_the_precompile_set()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Spec.IsPrecompile(Identity), Is.EqualTo(!eip7666Enabled), "IsPrecompile");
            Assert.That(Spec.Precompiles.Contains(Identity), Is.EqualTo(!eip7666Enabled), "Precompiles");
            Assert.That(Spec.ListPrecompiles().Values.Contains(Identity), Is.EqualTo(!eip7666Enabled), "eth_config precompiles");
            Assert.That(CodeInfoRepository.GetPrecompile(Identity, Spec), eip7666Enabled ? Is.Null : Is.SameAs(IdentityPrecompile.Instance), "dispatch");
            Assert.That(Spec.IsPrecompile(Address.FromNumber(3)) && Spec.IsPrecompile(Address.FromNumber(5)), Is.True, "the neighbouring precompiles stay");
        }
    }

    [Test]
    public void Identity_returns_its_input(
        [Values(Instruction.CALL, Instruction.STATICCALL, Instruction.DELEGATECALL)] Instruction callType,
        [Values(0, 1, 32, 33, 96)] int size)
    {
        byte[] input = Input(size);
        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(0, input)
            .Data(CallBytes(callType, size))
            .PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.RETURNDATASIZE).PushData(1).Op(Instruction.SSTORE)
            .Op(Instruction.RETURNDATASIZE).PushData(0).PushData(ReturnCopyOffset).Op(Instruction.RETURNDATACOPY)
            .Op(Instruction.RETURNDATASIZE).PushData(ReturnCopyOffset).Op(Instruction.KECCAK256).PushData(2).Op(Instruction.SSTORE)
            .Done;

        TestAllTracerWithOutput result = Execute(Activation, 1_000_000, code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success), result.Error);
        AssertStorage(0, UInt256.One);
        AssertStorage(1, (UInt256)size);
        AssertStorage(2, Keccak.Compute(input));
    }

    /// <remarks>
    /// The measured span is everything between two GAS readings: the call's pushes and the closing GAS cost the same
    /// either way, so the expected values differ only in the access cost and in precompile versus bytecode pricing.
    /// The EVM code costs CALLDATASIZE, PUSH0 and PUSH0 (2 each), CALLDATACOPY (3 + 3 per word + the memory
    /// expansion of 3 per word), CALLDATASIZE and PUSH0 (2 each), and RETURN with no further expansion (0).
    /// </remarks>
    [Test]
    public void Call_costs_ordinary_execution_and_a_cold_access(
        [Values(Instruction.CALL, Instruction.STATICCALL, Instruction.DELEGATECALL)] Instruction callType,
        [Values(0, 1, 32, 33, 96)] int size,
        [Values] bool prewarmed)
    {
        Prepare code = Prepare.EvmCode.StoreDataInMemory(0, Input(size));
        if (prewarmed)
        {
            code.PushData(Identity).Op(Instruction.BALANCE).Op(Instruction.POP);
        }

        code.Op(Instruction.GAS)
            .Data(CallBytes(callType, size))
            .Op(Instruction.GAS)
            .Op(Instruction.SWAP1).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.SWAP1).Op(Instruction.SUB).PushData(1).Op(Instruction.SSTORE);

        TestAllTracerWithOutput result = Execute(Activation, 1_000_000, code.Done);

        ulong words = (ulong)(size + 31) / 32;
        ulong pushes = callType == Instruction.CALL ? 7ul * GasCostOf.VeryLow : 6ul * GasCostOf.VeryLow;
        ulong access = eip7666Enabled && !prewarmed ? Eip8038Constants.ColdAccountAccess : GasCostOf.WarmStateRead;
        ulong execution = eip7666Enabled
            ? 5 * GasCostOf.Base + GasCostOf.VeryLow + GasCostOf.Memory * words + GasCostOf.Memory * words
            : 15 + 3 * words;

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success), result.Error);
        AssertStorage(0, UInt256.One);
        AssertStorage(1, (UInt256)(pushes + access + execution + GasCostOf.Base));
    }

    [Test]
    public void Replacement_code_is_observable_as_ordinary_code()
    {
        byte[] code = Prepare.EvmCode
            .PushData(Identity).Op(Instruction.EXTCODESIZE).PushData(0).Op(Instruction.SSTORE)
            .PushData(Identity).Op(Instruction.EXTCODEHASH).PushData(1).Op(Instruction.SSTORE)
            .Done;

        Execute(Activation, 1_000_000, code);

        AssertStorage(0, eip7666Enabled ? (UInt256)Eip7666Constants.IdentityCode.Length : UInt256.Zero);
        AssertStorage(1, eip7666Enabled ? Keccak.Compute(Eip7666Constants.IdentityCode.Span) : Keccak.Zero);
    }

    private static byte[] Input(int size) => Enumerable.Range(1, size).Select(static i => (byte)i).ToArray();

    // Every PushData is a PUSHn (VeryLow); nothing is returned into memory, so the caller pays no expansion.
    private static byte[] CallBytes(Instruction callType, int size)
    {
        Prepare call = Prepare.EvmCode.PushData(0).PushData(0).PushData(size).PushData(0);
        if (callType == Instruction.CALL)
        {
            call.PushData(0);
        }

        return call.PushData(Identity).PushData((long)CallGas).Op(callType).Done;
    }
}
