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
/// ADDMOD, MULMOD, DIV and MOD through <see cref="Arith256Accelerators"/>, with managed stand-ins for the
/// ZisK routines. The stand-ins count their calls and compute through <see cref="UInt256"/>, so each test
/// checks the plumbing - operand order, the zero-modulus and zero-divisor branches, the uninstalled
/// fallback - against a <see cref="BigInteger"/> oracle rather than the routines' own arithmetic.
/// </summary>
[NonParallelizable]
public unsafe class GuestArith256Tests
{
    private static readonly UInt256 Max = UInt256.MaxValue;
    private static readonly UInt256 High = UInt256.One << 255;

    private static int _addModCalls, _mulModCalls, _reduceModCalls, _divRemCalls;

    [SetUp]
    public void ResetCounters() => _addModCalls = _mulModCalls = _reduceModCalls = _divRemCalls = 0;

    [TearDown]
    public void Uninstall() => Arith256Accelerators.Install(null, null, null, null);

    private static void InstallStandIns() => Arith256Accelerators.Install(&AddMod, &MulMod, &ReduceMod, &DivRem);

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
    public void Installed_routines_compute_the_three_operand_opcodes(Instruction op, UInt256 a, UInt256 b, UInt256 m)
    {
        InstallStandIns();

        UInt256 result = Run3(op, a, b, m);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(Expected3(op, a, b, m)));
            Assert.That(op == Instruction.ADDMOD ? _addModCalls : _mulModCalls, Is.EqualTo(1), "the installed routine did not run");
        }
    }

    [TestCaseSource(nameof(TwoOperandCases))]
    public void Installed_routines_compute_the_two_operand_opcodes(Instruction op, UInt256 a, UInt256 b)
    {
        InstallStandIns();

        UInt256 result = Run2(op, a, b);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(Expected2(op, a, b)));
            Assert.That(op == Instruction.DIV ? _divRemCalls : _reduceModCalls, Is.EqualTo(1), "the installed routine did not run");
        }
    }

    [TestCase(Instruction.ADDMOD, 0ul)]
    [TestCase(Instruction.MULMOD, 0ul)]
    [TestCase(Instruction.DIV, 0ul)]
    [TestCase(Instruction.MOD, 0ul)]
    [TestCase(Instruction.MOD, 1ul)]
    public void A_zero_modulus_or_divisor_never_reaches_a_routine(Instruction op, ulong divisor)
    {
        InstallStandIns();

        UInt256 result = op is Instruction.ADDMOD or Instruction.MULMOD
            ? Run3(op, Max, Max, divisor)
            : Run2(op, Max, divisor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(UInt256.Zero));
            Assert.That(_addModCalls + _mulModCalls + _reduceModCalls + _divRemCalls, Is.Zero,
                "the routines' contract excludes a zero modulus or divisor; the opcode must answer those itself");
        }
    }

    [TestCaseSource(nameof(ThreeOperandCases))]
    public void Without_routines_the_three_operand_opcodes_keep_the_software_path(Instruction op, UInt256 a, UInt256 b, UInt256 m)
    {
        UInt256 result = Run3(op, a, b, m);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(Expected3(op, a, b, m)));
            Assert.That(_addModCalls + _mulModCalls, Is.Zero);
        }
    }

    [TestCaseSource(nameof(TwoOperandCases))]
    public void Without_routines_the_two_operand_opcodes_keep_the_software_path(Instruction op, UInt256 a, UInt256 b)
    {
        UInt256 result = Run2(op, a, b);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(Expected2(op, a, b)));
            Assert.That(_reduceModCalls + _divRemCalls, Is.Zero);
        }
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

    private static void AddMod(ulong* a, ulong* b, ulong* m, ulong* result)
    {
        _addModCalls++;
        UInt256.AddMod(in *(UInt256*)a, in *(UInt256*)b, in *(UInt256*)m, out *(UInt256*)result);
    }

    private static void MulMod(ulong* a, ulong* b, ulong* m, ulong* result)
    {
        _mulModCalls++;
        UInt256.MultiplyMod(in *(UInt256*)a, in *(UInt256*)b, in *(UInt256*)m, out *(UInt256*)result);
    }

    private static void ReduceMod(ulong* a, ulong* m, ulong* result)
    {
        _reduceModCalls++;
        UInt256.Mod(in *(UInt256*)a, in *(UInt256*)m, out *(UInt256*)result);
    }

    private static void DivRem(ulong* a, ulong* b, ulong* quotient, ulong* remainder)
    {
        _divRemCalls++;
        UInt256.Divide(in *(UInt256*)a, in *(UInt256*)b, out *(UInt256*)quotient);
        UInt256.Mod(in *(UInt256*)a, in *(UInt256*)b, out *(UInt256*)remainder);
    }
}
