// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis.IlEvm;

/// <summary>
/// Differential fuzzing: deterministic pseudo-random programs over the IL-EVM emittable opcode
/// set, executed through the real VM with IL-EVM off and on. Output bytes, remaining gas, and
/// the error outcome must be identical — this is the net that catches value-semantics bugs
/// hand-written cases miss (boundary shifts, sign handling, division edge cases).
/// </summary>
[TestFixture]
[NonParallelizable]
public class IlEvmDifferentialFuzzTests
{
    private const int ProgramCount = 150;

    private static readonly Instruction[] OperationPool =
    [
        Instruction.ADD, Instruction.SUB, Instruction.MUL, Instruction.DIV, Instruction.SDIV,
        Instruction.MOD, Instruction.SMOD, Instruction.SIGNEXTEND, Instruction.ADDMOD, Instruction.MULMOD,
        Instruction.SHL, Instruction.SHR, Instruction.SAR, Instruction.BYTE,
        Instruction.AND, Instruction.OR, Instruction.XOR, Instruction.NOT,
        Instruction.LT, Instruction.GT, Instruction.SLT, Instruction.SGT, Instruction.EQ, Instruction.ISZERO,
        Instruction.DUP1, Instruction.DUP2, Instruction.DUP3, Instruction.DUP4,
        Instruction.SWAP1, Instruction.SWAP2, Instruction.SWAP3,
        Instruction.POP, Instruction.PUSH1, Instruction.PUSH2, Instruction.PUSH32,
    ];

    private static readonly byte[] BoundaryBytes = [0x00, 0x01, 0x02, 0x1F, 0x20, 0x7F, 0x80, 0xFF];

    private bool _enabledBackup;
    private int _thresholdBackup;

    [SetUp]
    public void SetUp()
    {
        _enabledBackup = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled;
        _thresholdBackup = Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold;
    }

    [TearDown]
    public void TearDown()
    {
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = _enabledBackup;
        Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = _thresholdBackup;
    }

    [Test]
    public void Execute_RandomEmittablePrograms_IlEvmMatchesInterpreter()
    {
        for (int seed = 0; seed < ProgramCount; seed++)
        {
            byte[] code = GenerateProgram(new Random(seed));

            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = false;
            IlEvmTestExecutor.ExecutionResult interpreted = IlEvmTestExecutor.Run(new CodeInfo(code), gasLimit: 200_000);

            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.Enabled = true;
            Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.CompileThreshold = 1;
            IlEvmTestExecutor.ExecutionResult compiled = IlEvmTestExecutor.Run(new CodeInfo(code), gasLimit: 200_000);

            Assert.That(compiled.IsError, Is.EqualTo(interpreted.IsError), $"seed {seed}: error outcome diverged ({Convert.ToHexString(code)})");
            Assert.That(compiled.GasLeft, Is.EqualTo(interpreted.GasLeft), $"seed {seed}: gas diverged ({Convert.ToHexString(code)})");
            Assert.That(compiled.Output, Is.EqualTo(interpreted.Output), $"seed {seed}: output diverged ({Convert.ToHexString(code)})");
        }
    }

    /// <summary>
    /// Seeds the stack with 4-8 boundary-biased PUSHes, applies 10-40 random operations, then
    /// stores the top three stack values to memory and returns them — so value divergence
    /// anywhere near the top of the stack becomes observable output divergence. Programs that
    /// underflow fail identically on both sides, which the error comparison covers.
    /// </summary>
    private static byte[] GenerateProgram(Random rng)
    {
        System.Collections.Generic.List<byte> code = [];

        int seedValues = rng.Next(4, 9);
        for (int i = 0; i < seedValues; i++)
            EmitRandomPush(rng, code);

        int operations = rng.Next(10, 41);
        for (int i = 0; i < operations; i++)
        {
            Instruction op = OperationPool[rng.Next(OperationPool.Length)];
            switch (op)
            {
                case Instruction.PUSH1 or Instruction.PUSH2 or Instruction.PUSH32:
                    EmitRandomPush(rng, code);
                    break;
                default:
                    code.Add((byte)op);
                    break;
            }
        }

        for (int slot = 0; slot < 3; slot++)
        {
            code.Add((byte)Instruction.PUSH1);
            code.Add((byte)(slot * 32));
            code.Add((byte)Instruction.MSTORE);
        }
        code.Add((byte)Instruction.PUSH1);
        code.Add(96);
        code.Add((byte)Instruction.PUSH1);
        code.Add(0);
        code.Add((byte)Instruction.RETURN);

        return [.. code];
    }

    private static void EmitRandomPush(Random rng, System.Collections.Generic.List<byte> code)
    {
        int form = rng.Next(3);
        if (form == 0)
        {
            code.Add((byte)Instruction.PUSH1);
            code.Add(RandomByte(rng));
        }
        else if (form == 1)
        {
            code.Add((byte)Instruction.PUSH2);
            code.Add(RandomByte(rng));
            code.Add(RandomByte(rng));
        }
        else
        {
            code.Add((byte)Instruction.PUSH32);
            for (int i = 0; i < 32; i++)
                code.Add(RandomByte(rng));
        }
    }

    /// <summary>Half boundary values, half uniform — shifts and sign extension live on the edges.</summary>
    private static byte RandomByte(Random rng) =>
        rng.Next(2) == 0 ? BoundaryBytes[rng.Next(BoundaryBytes.Length)] : (byte)rng.Next(256);
}
