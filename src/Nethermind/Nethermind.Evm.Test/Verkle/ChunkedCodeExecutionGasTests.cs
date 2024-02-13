// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Verkle;

public class ChunkedCodeExecutionGasTests: VerkleVirtualMachineTestsBase
{
    [Test]
    public void TestBeforeVerkle()
    {
        var code = new byte[]
        {
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
        };
        TestAllTracerWithOutput receipt = Execute(BlockNumber - 1, 100000, code);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 45 ));
    }

    [Test]
    public void TestAfterVerkle()
    {
        var code = new byte[]
        {
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
        };
        TestAllTracerWithOutput receipt = Execute(BlockNumber, 100000, code);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 45 + (GasCostOf.WitnessChunkRead * (int)Math.Ceiling((double)code.Length / 31))));
    }
}
