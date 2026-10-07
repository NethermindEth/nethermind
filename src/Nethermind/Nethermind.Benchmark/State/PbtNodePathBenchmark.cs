// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Pbt;

namespace Nethermind.Benchmarks.State;

[MemoryDiagnoser]
public class PbtNodePathBenchmark
{
    private byte[] _bytes;

    [Params(0, 1, 8, 64, 272, 524, 528)]
    public int BitDepth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bytes = new byte[(BitDepth + 7) >> 3];
        new Random(8297).NextBytes(_bytes);
        if ((BitDepth & 7) != 0) _bytes[^1] &= (byte)(0xFF << (8 - (BitDepth & 7)));
    }

    [Benchmark]
    public PbtStorageNodePath Construct() => new(_bytes, BitDepth);
}

[MemoryDiagnoser]
[GenericTypeArguments(typeof(PbtNodePath))]
[GenericTypeArguments(typeof(PbtStorageNodePath))]
public class PbtNodePathMemoryBenchmark<TPath> where TPath : struct, IPbtNodePath<TPath>
{
    private byte[] _bytes;

    [Params(0, 8, 272)]
    public int BitDepth { get; set; }

    [GlobalSetup]
    public void Setup() => _bytes = new byte[(BitDepth + 7) / 8];

    [Benchmark]
    public TPath Construct() => TPath.Create(_bytes, BitDepth);
}
