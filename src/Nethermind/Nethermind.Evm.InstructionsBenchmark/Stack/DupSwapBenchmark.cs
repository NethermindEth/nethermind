// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Evm.InstructionsBenchmark.Helpers;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark.Stack;

/// <summary>
/// Benchmarks for DUP1-DUP16 and SWAP1-SWAP16 operations.
/// Run: dotnet run -c Release --filter "*DupSwapBenchmark*"
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class DupSwapBenchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _stackBuffer = BenchmarkHelpers.CreateStackBuffer();
        _codeBuffer = new byte[1];

        // Pre-fill 20 stack slots with unique values for testing
        for (int i = 0; i < 20; i++)
        {
            UInt256 value = (UInt256)(i + 1) * 0x1111111111111111UL;
            BenchmarkHelpers.WriteStackSlot(_stackBuffer, i, in value);
        }
    }

    // DUP benchmarks
    [Benchmark(Baseline = true)]
    public EvmExceptionType Dup1()
    {
        var stack = new EvmStack(5, ref _stackBuffer[0], _codeBuffer);
        return stack.Dup<OffFlag>(1);
    }

    [Benchmark]
    public EvmExceptionType Dup2()
    {
        var stack = new EvmStack(5, ref _stackBuffer[0], _codeBuffer);
        return stack.Dup<OffFlag>(2);
    }

    [Benchmark]
    public EvmExceptionType Dup4()
    {
        var stack = new EvmStack(5, ref _stackBuffer[0], _codeBuffer);
        return stack.Dup<OffFlag>(4);
    }

    [Benchmark]
    public EvmExceptionType Dup8()
    {
        var stack = new EvmStack(10, ref _stackBuffer[0], _codeBuffer);
        return stack.Dup<OffFlag>(8);
    }

    [Benchmark]
    public EvmExceptionType Dup16()
    {
        var stack = new EvmStack(18, ref _stackBuffer[0], _codeBuffer);
        return stack.Dup<OffFlag>(16);
    }

    // SWAP benchmarks
    [Benchmark]
    public EvmExceptionType Swap1()
    {
        var stack = new EvmStack(5, ref _stackBuffer[0], _codeBuffer);
        return stack.Swap<OffFlag>(2); // SWAP1 swaps top with 2nd element (depth 2)
    }

    [Benchmark]
    public EvmExceptionType Swap2()
    {
        var stack = new EvmStack(5, ref _stackBuffer[0], _codeBuffer);
        return stack.Swap<OffFlag>(3);
    }

    [Benchmark]
    public EvmExceptionType Swap4()
    {
        var stack = new EvmStack(6, ref _stackBuffer[0], _codeBuffer);
        return stack.Swap<OffFlag>(5);
    }

    [Benchmark]
    public EvmExceptionType Swap8()
    {
        var stack = new EvmStack(10, ref _stackBuffer[0], _codeBuffer);
        return stack.Swap<OffFlag>(9);
    }

    [Benchmark]
    public EvmExceptionType Swap16()
    {
        var stack = new EvmStack(18, ref _stackBuffer[0], _codeBuffer);
        return stack.Swap<OffFlag>(17);
    }

    // Exchange benchmarks (EOF)
    [Benchmark]
    public bool Exchange_1_2()
    {
        var stack = new EvmStack(5, ref _stackBuffer[0], _codeBuffer);
        return stack.Exchange<OffFlag>(1, 2);
    }

    [Benchmark]
    public bool Exchange_1_8()
    {
        var stack = new EvmStack(10, ref _stackBuffer[0], _codeBuffer);
        return stack.Exchange<OffFlag>(1, 8);
    }

    [Benchmark]
    public bool Exchange_4_8()
    {
        var stack = new EvmStack(10, ref _stackBuffer[0], _codeBuffer);
        return stack.Exchange<OffFlag>(4, 8);
    }

    // EnsureDepth benchmark (used for stack validation)
    [Benchmark]
    public bool EnsureDepth_Sufficient()
    {
        var stack = new EvmStack(10, ref _stackBuffer[0], _codeBuffer);
        return stack.EnsureDepth(8);
    }

    [Benchmark]
    public bool EnsureDepth_Insufficient()
    {
        var stack = new EvmStack(5, ref _stackBuffer[0], _codeBuffer);
        return stack.EnsureDepth(10);
    }
}
