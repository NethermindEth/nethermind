// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>
/// ADDMOD, MULMOD, DIV and MOD in the zkEVM build, against a <see cref="BigInteger"/> oracle. On the host
/// <see cref="ZiskArith256Flag"/> is off, so these pin the software path every guest but ZisK runs; the
/// ZisK routines themselves run only in the guest, whose stateless-tests blocks check them.
/// </summary>
public class GuestArith256Tests
{
    private static readonly UInt256 Max = UInt256.MaxValue;
    private static readonly UInt256 High = UInt256.One << 255;

    private static IEnumerable<TestCaseData> ThreeOperandCases()
    {
        foreach (Instruction op in new[] { Instruction.ADDMOD, Instruction.MULMOD })
        {
            // The sum or product overflows 256 bits, so a routine that drops the carry gets these wrong.
            yield return new TestCaseData(op, Max, Max, Max).SetName($"{op} max, max mod max");
            yield return new TestCaseData(op, Max, Max, Max - 1).SetName($"{op} max, max mod max-1");
            yield return new TestCaseData(op, High, High, (UInt256)3).SetName($"{op} 2^255, 2^255 mod 3");
            // Operands above the modulus, so none of them arrives already reduced.
            yield return new TestCaseData(op, (UInt256)1_000_003, Max - 7, (UInt256)97).SetName($"{op} unreduced mod 97");
            yield return new TestCaseData(op, (UInt256)5, (UInt256)7, UInt256.One).SetName($"{op} mod 1");
            yield return new TestCaseData(op, UInt256.Zero, Max, (UInt256)11).SetName($"{op} zero operand");
        }
    }

    private static IEnumerable<TestCaseData> TwoOperandCases()
    {
        foreach (Instruction op in new[] { Instruction.DIV, Instruction.MOD })
        {
            yield return new TestCaseData(op, Max, (UInt256)2).SetName($"{op} max by 2");
            yield return new TestCaseData(op, Max, Max - 1).SetName($"{op} max by max-1");
            yield return new TestCaseData(op, (UInt256)3, Max).SetName($"{op} small by max");
            yield return new TestCaseData(op, High + 12345, (UInt256)0xffff_ffff_ffff_fffbUL).SetName($"{op} by a 64-bit divisor");
            yield return new TestCaseData(op, Max, High + 1).SetName($"{op} by a 256-bit divisor");
        }
    }

    [TestCaseSource(nameof(ThreeOperandCases))]
    public void Three_operand_opcodes_match_the_oracle(Instruction op, UInt256 a, UInt256 b, UInt256 m) =>
        Assert.That(Run3(op, a, b, m), Is.EqualTo(Expected3(op, a, b, m)));

    [TestCaseSource(nameof(TwoOperandCases))]
    public void Two_operand_opcodes_match_the_oracle(Instruction op, UInt256 a, UInt256 b) =>
        Assert.That(Run2(op, a, b), Is.EqualTo(Expected2(op, a, b)));

    [TestCase(Instruction.ADDMOD, 0ul)]
    [TestCase(Instruction.MULMOD, 0ul)]
    [TestCase(Instruction.DIV, 0ul)]
    [TestCase(Instruction.MOD, 0ul)]
    [TestCase(Instruction.MOD, 1ul)]
    public void The_opcode_answers_a_zero_modulus_or_divisor_itself(Instruction op, ulong divisor)
    {
        UInt256 result = op is Instruction.ADDMOD or Instruction.MULMOD
            ? Run3(op, Max, Max, divisor)
            : Run2(op, Max, divisor);

        Assert.That(result, Is.EqualTo(UInt256.Zero));
    }

    /// <summary>Runs ADDMOD or MULMOD on <c>a</c> (top), <c>b</c> and <c>m</c>, the way the interpreter pops them.</summary>
    private static UInt256 Run3(Instruction op, UInt256 a, UInt256 b, UInt256 m)
    {
        byte[] buffer = new byte[4 * EvmStack.WordSize];
        EvmStack stack = new(0, ref buffer[0], ReadOnlySpan<byte>.Empty, null);
        stack.PushUInt256<OffFlag>(in m);
        stack.PushUInt256<OffFlag>(in b);
        stack.PushUInt256<OffFlag>(in a);
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(100);

        EvmExceptionType status = op == Instruction.ADDMOD
            ? EvmInstructions.InstructionMath3Param<EthereumGasPolicy, EvmInstructions.OpAddMod, OffFlag>(ref stack, ref gas, null!)
            : EvmInstructions.InstructionMath3Param<EthereumGasPolicy, EvmInstructions.OpMulMod, OffFlag>(ref stack, ref gas, null!);

        Assert.That(status, Is.EqualTo(EvmExceptionType.None));
        Assert.That(stack.PopUInt256(out UInt256 result), Is.True);
        return result;
    }

    /// <summary>Runs DIV or MOD on <c>a</c> (top) and <c>b</c>.</summary>
    private static UInt256 Run2(Instruction op, UInt256 a, UInt256 b)
    {
        byte[] buffer = new byte[3 * EvmStack.WordSize];
        EvmStack stack = new(0, ref buffer[0], ReadOnlySpan<byte>.Empty, null);
        stack.PushUInt256<OffFlag>(in b);
        stack.PushUInt256<OffFlag>(in a);
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(100);

        EvmExceptionType status = op == Instruction.DIV
            ? EvmInstructions.InstructionMath2Param<EthereumGasPolicy, EvmInstructions.OpDiv, OffFlag>(ref stack, ref gas)
            : EvmInstructions.InstructionMath2Param<EthereumGasPolicy, EvmInstructions.OpMod, OffFlag>(ref stack, ref gas);

        Assert.That(status, Is.EqualTo(EvmExceptionType.None));
        Assert.That(stack.PopUInt256(out UInt256 result), Is.True);
        return result;
    }

    private static UInt256 Expected3(Instruction op, UInt256 a, UInt256 b, UInt256 m)
    {
        if (m.IsZero) return UInt256.Zero;
        BigInteger value = op == Instruction.ADDMOD ? (BigInteger)a + (BigInteger)b : (BigInteger)a * (BigInteger)b;
        return (UInt256)(value % (BigInteger)m);
    }

    private static UInt256 Expected2(Instruction op, UInt256 a, UInt256 b)
    {
        if (b.IsZero) return UInt256.Zero;
        return (UInt256)(op == Instruction.DIV ? (BigInteger)a / (BigInteger)b : (BigInteger)a % (BigInteger)b);
    }
}
