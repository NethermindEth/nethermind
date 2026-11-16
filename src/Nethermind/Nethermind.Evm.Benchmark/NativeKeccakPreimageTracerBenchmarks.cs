// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Preimage;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark;

/// <summary>Measures the preimage tracer's KECCAK256 capture when a transaction hashes the same mapping slots repeatedly.</summary>
[MemoryDiagnoser]
public class NativeKeccakPreimageTracerBenchmarks
{
    private const int PreimageLength = 64;
    private const int DistinctPreimages = 4;
    private const int KeccakCount = 64;

    private readonly Transaction _transaction = Build.A.Transaction.TestObject;
    private readonly ExecutionEnvironment _env = null!;
    private TraceMemory _memory;
    private TraceStack[] _stacks = null!;
    private NativeKeccakPreimageTracer _warmTracer = null!;

    [GlobalSetup]
    public void Setup()
    {
        byte[] memory = new byte[PreimageLength * DistinctPreimages];
        new Random(42).NextBytes(memory);
        _memory = new TraceMemory((ulong)memory.Length, memory);

        _stacks = new TraceStack[DistinctPreimages];
        for (int i = 0; i < DistinctPreimages; i++)
        {
            byte[] words = new byte[2 * EvmStack.WordSize];
            new UInt256(PreimageLength).ToBigEndian(words.AsSpan(0, EvmStack.WordSize));
            new UInt256((ulong)(i * PreimageLength)).ToBigEndian(words.AsSpan(EvmStack.WordSize, EvmStack.WordSize));
            _stacks[i] = new TraceStack(words);
        }

        _warmTracer = new NativeKeccakPreimageTracer(_transaction, new GethTraceOptions());
        TraceKeccaks(_warmTracer);
        if (((Dictionary<Hash256, byte[]>)_warmTracer.BuildResult().CustomTracerResult!.Value!).Count != DistinctPreimages)
        {
            throw new InvalidOperationException("The tracer must capture every distinct preimage.");
        }
    }

    [Benchmark(OperationsPerInvoke = KeccakCount)]
    public void RepeatedKeccak() => TraceKeccaks(_warmTracer);

    [Benchmark(OperationsPerInvoke = KeccakCount)]
    public object TraceTransaction()
    {
        using NativeKeccakPreimageTracer tracer = new(_transaction, new GethTraceOptions());
        TraceKeccaks(tracer);
        return tracer.BuildResult().CustomTracerResult!.Value;
    }

    [GlobalCleanup]
    public void Cleanup() => _warmTracer?.Dispose();

    private void TraceKeccaks(NativeKeccakPreimageTracer tracer)
    {
        for (int i = 0; i < KeccakCount; i++)
        {
            tracer.StartOperation(0, Instruction.KECCAK256, 0, in _env);
            tracer.SetOperationMemory(_memory);
            tracer.SetOperationStack(_stacks[i % DistinctPreimages]);
        }
    }
}
