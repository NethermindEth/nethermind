// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.VirtualMachine;

/// <summary>
/// Benchmarks for core VM execution paths.
/// These benchmarks isolate specific components of the execution pipeline.
/// Run: dotnet run -c Release --filter "*ExecuteOpcodesBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class ExecuteOpcodesBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    // Simple bytecode: PUSH1 0x01, PUSH1 0x02, ADD, POP (repeated)
    private static readonly byte[] SimpleLoop = CreateSimpleLoop(100);

    // Bytecode with jumps: PUSH1 dest, JUMP, JUMPDEST, PUSH1 1, POP, ...
    private static readonly byte[] JumpLoop = CreateJumpLoop(50);

    [GlobalSetup]
    public void Setup()
    {
        _stackBuffer = BenchmarkHelpers.CreateStackBuffer();
        _codeBuffer = new byte[4096];
        SimpleLoop.AsSpan().CopyTo(_codeBuffer);
    }

    /// <summary>
    /// Benchmark the OpcodeResult structure packing/unpacking.
    /// This is used millions of times in the hot loop.
    /// </summary>
    [Benchmark(Baseline = true)]
    public OpcodeResult OpcodeResult_PackUnpack()
    {
        int pc = 42;
        EvmExceptionType ex = EvmExceptionType.None;
        var result = new OpcodeResult(pc, ex);

        // Simulate loop termination check
        if (result.Value < 1000)
        {
            return new OpcodeResult(result.ProgramCounter + 1);
        }
        return result;
    }

    [Benchmark]
    public OpcodeResult OpcodeResult_ExceptionPath()
    {
        int pc = 42;
        EvmExceptionType ex = EvmExceptionType.OutOfGas;
        return new OpcodeResult(pc, ex);
    }

    /// <summary>
    /// Benchmark the loop termination condition.
    /// This tests: while (result.Value < codeLength)
    /// </summary>
    [Benchmark]
    public int LoopTermination_Continue()
    {
        ulong resultValue = 50;  // PC = 50
        uint codeLength = 100;
        int iterations = 0;

        // Simulate the hot loop condition
        while (resultValue < codeLength)
        {
            resultValue++;
            iterations++;
            if (iterations >= 50) break;  // Prevent infinite
        }
        return iterations;
    }

    [Benchmark]
    public int LoopTermination_Exception()
    {
        // Exception encoded in high bits makes Value >= codeLength
        ulong resultValue = ((ulong)(uint)EvmExceptionType.OutOfGas << 32) | 50;
        uint codeLength = 100;

        // This should immediately exit
        if (resultValue < codeLength)
        {
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// Benchmark simple stack operations in a loop pattern.
    /// </summary>
    [Benchmark]
    public int StackOperations_PushPop()
    {
        var stack = new EvmStack(0, ref _stackBuffer[0], _codeBuffer);

        // Simulate repeated PUSH/POP pattern
        for (int i = 0; i < 100; i++)
        {
            stack.PushOne<OffFlag>();
            stack.PopLimbo();
        }

        return stack.Head;
    }

    /// <summary>
    /// Benchmark instruction fetch pattern.
    /// </summary>
    [Benchmark]
    public Instruction InstructionFetch_Pattern()
    {
        ref byte code = ref _codeBuffer[0];
        Instruction result = default;

        // Simulate fetching instructions like in ExecuteOpcodes
        for (nuint i = 0; i < 100; i++)
        {
            result = (Instruction)Unsafe.Add(ref code, i);
        }

        return result;
    }

    private static byte[] CreateSimpleLoop(int iterations)
    {
        // PUSH1 0x01, PUSH1 0x02, ADD, POP pattern
        var code = new byte[iterations * 6];
        for (int i = 0; i < iterations; i++)
        {
            int offset = i * 6;
            code[offset] = (byte)Instruction.PUSH1;
            code[offset + 1] = 0x01;
            code[offset + 2] = (byte)Instruction.PUSH1;
            code[offset + 3] = 0x02;
            code[offset + 4] = (byte)Instruction.ADD;
            code[offset + 5] = (byte)Instruction.POP;
        }
        return code;
    }

    private static byte[] CreateJumpLoop(int iterations)
    {
        // Create a simple jump-based loop
        // JUMPDEST, PUSH1 1, PUSH1 0, JUMPI (conditional exit)
        var code = new byte[iterations * 10 + 1];
        code[0] = (byte)Instruction.JUMPDEST;

        for (int i = 0; i < iterations; i++)
        {
            int offset = 1 + i * 10;
            code[offset] = (byte)Instruction.PUSH1;
            code[offset + 1] = 0x01;
            code[offset + 2] = (byte)Instruction.PUSH1;
            code[offset + 3] = 0x00;
            code[offset + 4] = (byte)Instruction.ADD;
            code[offset + 5] = (byte)Instruction.POP;
            code[offset + 6] = (byte)Instruction.PUSH1;
            code[offset + 7] = 0x00;  // Jump to start
            code[offset + 8] = (byte)Instruction.PUSH1;
            code[offset + 9] = 0x00;  // Condition (false = don't jump)
        }

        return code;
    }
}
