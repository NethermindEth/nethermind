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
    // The unscanned destinations are JUMPDEST bytes inside PUSH1 data.
    [TestCase("61000556605b00", 0, false, 3, 2, TestName = "Unscanned JUMP runs unfused")]
    [TestCase("61000557605b00", 1, false, 3, 2, TestName = "Unscanned taken JUMPI runs unfused")]
    [TestCase("61000557605b00", 0, false, 4, 0, TestName = "Unscanned not-taken JUMPI stays fused")]
    [TestCase("610004565b00", 0, true, 5, 1, TestName = "Scanned JUMP stays fused")]
    [TestCase("610004575b00", 1, true, 5, 0, TestName = "Odd-offset taken JUMPI stays fused")]
    [TestCase("5b610005565b00", 0, true, 6, 1, TestName = "Even-offset JUMP stays fused")]
    [TestCase("5b610005575b00", 1, true, 6, 0, TestName = "Even-offset taken JUMPI stays fused")]
    [TestCase("5b610005575b00", 0, false, 5, 0, TestName = "Even-offset not-taken JUMPI stays fused")]
    public void Push2_fuses_only_on_a_known_destination(string hex, byte top, bool scanned,
        int expectedProgramCounter, int expectedHead)
    {
        byte[] code = Convert.FromHexString(hex);
        CodeInfo codeInfo = new(code);
        byte[] memory = new byte[4 * EvmStack.WordSize];
        EvmStack stack = new(0, ref memory[0], code, codeInfo);
        stack.PushByte<OffFlag, OnFlag>(top);
        nint programCounter = Array.IndexOf(code, (byte)Instruction.PUSH2) + 1;
        int destination = (code[(int)programCounter] << 8) | code[(int)programCounter + 1];
        if (scanned) Assert.That(stack.IsJumpDestination(destination), Is.True);
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(100_000);
        // The untraced guest handler counts no opcodes, so it never touches the machine.
        VirtualMachine<EthereumGasPolicy> vm = (VirtualMachine<EthereumGasPolicy>)RuntimeHelpers.GetUninitializedObject(typeof(VirtualMachine));

        EvmExceptionType result = EvmInstructions.InstructionPush2<EthereumGasPolicy, OffFlag>(ref stack, ref gas, vm, ref programCounter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(EvmExceptionType.None));
            Assert.That(programCounter, Is.EqualTo((nint)expectedProgramCounter), "program counter");
            Assert.That(stack.Head, Is.EqualTo((nint)expectedHead), "stack depth");
        }
    }
}
