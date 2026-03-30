// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
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
        result.Error.Should().BeNull();
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
        result.Error.Should().BeNull();
    }

    [TestCase(Instruction.CALLDATACOPY)]
    [TestCase(Instruction.CODECOPY)]
    [TestCase(Instruction.EXTCODECOPY)]
    public void Copy_ZeroLength_ConsumesBaseGas(Instruction instruction)
    {
        byte[] code = BuildCopyCode(instruction, 0);

        TestAllTracerWithOutput result = Execute(code);

        result.Error.Should().BeNull();
        result.GasSpent.Should().Be(GetBaseGas(instruction));
    }

    [TestCase(Instruction.CALLDATACOPY)]
    [TestCase(Instruction.CODECOPY)]
    [TestCase(Instruction.EXTCODECOPY)]
    public void Copy_ZeroLength_InsufficientGas_ReturnsOutOfGas(Instruction instruction)
    {
        byte[] code = BuildCopyCode(instruction, 0);
        long gasLimit = GetBaseGas(instruction) - 1;

        TestAllTracerWithOutput result = Execute(Activation, gasLimit, code);

        result.Error.Should().Be("OutOfGas");
    }

    [TestCase(Instruction.CALLDATACOPY)]
    [TestCase(Instruction.CODECOPY)]
    [TestCase(Instruction.EXTCODECOPY)]
    public void Copy_OneWord_ConsumesMemoryGas(Instruction instruction)
    {
        byte[] code = BuildCopyCode(instruction, 32);

        TestAllTracerWithOutput result = Execute(code);

        result.Error.Should().BeNull();
        result.GasSpent.Should().Be(GetBaseGas(instruction) + 2 * GasCostOf.Memory);
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

    private static long GetBaseGas(Instruction instruction) => instruction == Instruction.EXTCODECOPY
        ? GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.ExtCodeEip150
        : GasCostOf.Transaction + 3 * GasCostOf.VeryLow + GasCostOf.VeryLow;
}
