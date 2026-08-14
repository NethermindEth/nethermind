// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class DataCopyGasTests : VirtualMachineTestsBase
{
    [Test]
    public void CallDataCopy_Ranges()
    {
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData("0x1e4e2")
            .PushData("0x5050600163306e2b386347355944f3636f376163636d6b")
            .Op(Instruction.CALLDATACOPY)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.Error, Is.Null);
    }

    [Test]
    public void ExtCodeCopy_Ranges()
    {
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .PushData("0x805e0d3cde3764a4d0a02f33cf624c8b7cfd911a")
            .PushData("0x793d1e")
            .Op(Instruction.EXTCODECOPY)
            .Done;

        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.Error, Is.Null);
    }

    [Test]
    public void CallDataCopy_zero_extends_past_input()
    {
        byte[] code = Prepare.EvmCode
            .CALLDATACOPY(0, 1, 5)
            .MLOAD(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (_, Transaction transaction) = PrepareTx(Activation, 100_000, code, new byte[] { 1, 2, 3 }, UInt256.Zero);
        TestAllTracerWithOutput tracer = Execute(transaction);

        byte[] expected = new byte[32];
        expected[0] = 2;
        expected[1] = 3;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.Null);
            AssertStorage(0, expected);
        }
    }

    [Test]
    public void CodeCopy_zero_extends_past_code()
    {
        byte[] code = Prepare.EvmCode
            .PushData(4)
            .PushData((byte)0)
            .PushData(0)
            .Op(Instruction.CODECOPY)
            .MLOAD(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;
        code[3] = (byte)(code.Length - 2);

        TestAllTracerWithOutput tracer = Execute(code);

        byte[] expected = new byte[32];
        expected[0] = code[^2];
        expected[1] = code[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.Null);
            AssertStorage(0, expected);
        }
    }

    [Test]
    public void ExtCodeCopy_zero_extends_past_external_code()
    {
        Address target = TestItem.AddressC;
        byte[] externalCode = new byte[] { 1, 2, 3 };
        TestState.CreateAccount(target, UInt256.Zero);
        TestState.InsertCode(target, externalCode, SpecProvider.GenesisSpec);
        byte[] code = Prepare.EvmCode
            .EXTCODECOPY(target, 0, 1, 5)
            .MLOAD(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        TestAllTracerWithOutput tracer = Execute(code);

        byte[] expected = new byte[32];
        expected[0] = 2;
        expected[1] = 3;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Error, Is.Null);
            AssertStorage(0, expected);
        }
    }

    [TestCase(Instruction.CALLDATACOPY)]
    [TestCase(Instruction.CODECOPY)]
    [TestCase(Instruction.EXTCODECOPY)]
    public void Copy_ZeroLength_ConsumesBaseGas(Instruction instruction)
    {
        byte[] code = BuildCopyCode(instruction, 0);

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.GasSpent, Is.EqualTo(GetBaseGas(instruction)));
    }

    [TestCase(Instruction.CALLDATACOPY)]
    [TestCase(Instruction.CODECOPY)]
    [TestCase(Instruction.EXTCODECOPY)]
    public void Copy_ZeroLength_InsufficientGas_ReturnsOutOfGas(Instruction instruction)
    {
        byte[] code = BuildCopyCode(instruction, 0);
        ulong gasLimit = GetBaseGas(instruction) - 1UL;

        TestAllTracerWithOutput result = Execute(Activation, gasLimit, code);

        Assert.That(result.Error, Is.EqualTo("OutOfGas"));
    }

    [TestCase(Instruction.CALLDATACOPY)]
    [TestCase(Instruction.CODECOPY)]
    [TestCase(Instruction.EXTCODECOPY)]
    public void Copy_OneWord_ConsumesMemoryGas(Instruction instruction)
    {
        byte[] code = BuildCopyCode(instruction, 32);

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.Error, Is.Null);
        Assert.That(result.GasSpent, Is.EqualTo(GetBaseGas(instruction) + 2UL * GasCostOf.Memory));
    }

    private static byte[] BuildCopyCode(Instruction instruction, int length)
    {
        Prepare prepare = Prepare.EvmCode
            .PushData(length)
            .PushData(0)
            .PushData(0);

        if (instruction == Instruction.EXTCODECOPY)
            prepare.PushData(TestItem.AddressC);

        return prepare.Op(instruction).Done;
    }

    private static ulong GetBaseGas(Instruction instruction) => instruction == Instruction.EXTCODECOPY
        ? GasCostOf.Transaction + 4UL * GasCostOf.VeryLow + GasCostOf.ExtCodeEip150
        : GasCostOf.Transaction + 3UL * GasCostOf.VeryLow + GasCostOf.VeryLow;
}
