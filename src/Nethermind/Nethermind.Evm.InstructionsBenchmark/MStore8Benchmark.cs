// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm.InstructionsBenchmark;

/// <summary>
/// Benchmark for MSTORE8 stack operations.
/// Tests show that separate PopUInt256 + PopByte calls are already well-optimized
/// by the JIT - the AVX512 VBMI vpermb instruction is very efficient.
/// Combined approaches added overhead without benefit.
/// </summary>
[DisassemblyDiagnoser(maxDepth: 3, exportGithubMarkdown: true, exportHtml: true)]
public class MStore8Benchmark
{
    private byte[] _stackBuffer = null!;
    private byte[] _codeBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Allocate a stack buffer with 2 elements (64 bytes)
        _stackBuffer = GC.AllocateArray<byte>(EvmStack.MaxStackSize, pinned: true);
        _codeBuffer = new byte[1]; // Minimal code buffer

        // Set up element 0: data byte (value = 0x42) at position 31 (big-endian)
        _stackBuffer[31] = 0x42;

        // Set up element 1: memory offset (value = 100) at position 63 (big-endian last byte)
        _stackBuffer[63] = 100;
    }

    [Benchmark(Baseline = true)]
    public (UInt256 offset, byte data) CurrentImplementation()
    {
        var stack = new EvmStack(2, ref _stackBuffer[0], _codeBuffer);

        // Current approach: two separate pops (JIT optimizes this well)
        stack.PopUInt256(out UInt256 offset);
        int dataResult = stack.PopByte();
        byte data = (byte)(uint)dataResult;

        return (offset, data);
    }
}
