// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis;

[TestFixture]
public class ElisionDebugTests
{
    [Test]
    public void DumpJumpLoopStream()
    {
        byte[] code = Prepare.EvmCode
            .PushData(5)
            .Op(Instruction.JUMPDEST)
            .PushData(1).Op(Instruction.SWAP1).Op(Instruction.SUB)
            .Op(Instruction.DUP1)
            .PushData(2).Op(Instruction.JUMPI)
            .Op(Instruction.STOP)
            .Done;

        InstructionStream stream = InstructionStream.TryBuild(code);
        Assert.That(stream, Is.Not.Null);
        for (int i = 0; i < stream.Ops.Length; i++)
        {
            StreamOp op = stream.Ops[i];
            Console.WriteLine($"entry {i}: opcode=0x{op.Opcode:X2} kind={op.Kind} pc={op.Pc} block={op.BlockIndex} advance={op.Advance}");
        }
        for (int b = 0; b < stream.BlockGas.Length; b++)
        {
            Console.WriteLine($"blockGas[{b}] = {stream.BlockGas[b]}");
        }
        for (int pc = 0; pc < code.Length + 1; pc++)
        {
            Console.WriteLine($"pcToEntry[{pc}] = {stream.PcToEntry[pc]}");
        }
    }
}
