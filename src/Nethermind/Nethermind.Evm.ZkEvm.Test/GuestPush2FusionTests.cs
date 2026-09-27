// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>The guest's PUSH2+JUMP(I) fusion, which fuses only on a destination the lazy scan has already reached.</summary>
public class GuestPush2FusionTests
{
    // Each code is PUSH2 <destination> followed by the jump; offset 5 is a JUMPDEST byte inside PUSH1 data.
    [TestCase("61000556605b00", 0, false, 3, 2, 0, TestName = "Unscanned JUMP runs unfused")]
    [TestCase("61000557605b00", 1, false, 3, 2, 0, TestName = "Unscanned taken JUMPI runs unfused")]
    [TestCase("61000557605b00", 0, false, 4, 0, 1, TestName = "Unscanned not-taken JUMPI stays fused")]
    [TestCase("610004565b00", 0, true, 5, 1, 2, TestName = "Scanned JUMP stays fused")]
    public void Push2_fuses_only_on_a_known_destination(string hex, byte top, bool scanned,
        int expectedProgramCounter, int expectedHead, int expectedOpCodeCount)
    {
        byte[] code = Convert.FromHexString(hex);
        CodeInfo codeInfo = new(code);
        byte[] memory = new byte[4 * EvmStack.WordSize];
        EvmStack stack = new(0, ref memory[0], code, codeInfo);
        stack.PushByte<OffFlag, OnFlag>(top);
        int destination = (code[1] << 8) | code[2];
        if (scanned) Assert.That(stack.IsJumpDestination(destination), Is.True);
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(100_000);
        // The untraced handler touches the machine only for its opcode count.
        VirtualMachine<EthereumGasPolicy> vm = (VirtualMachine<EthereumGasPolicy>)RuntimeHelpers.GetUninitializedObject(typeof(VirtualMachine));
        nint programCounter = 1;

        EvmExceptionType result = EvmInstructions.InstructionPush2<EthereumGasPolicy, OffFlag>(ref stack, ref gas, vm, ref programCounter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(EvmExceptionType.None));
            Assert.That(programCounter, Is.EqualTo((nint)expectedProgramCounter), "program counter");
            Assert.That(stack.Head, Is.EqualTo((nint)expectedHead), "stack depth");
            Assert.That(vm.OpCodeCount, Is.EqualTo(expectedOpCodeCount), "opcodes the fusion ran");
        }
    }
}
