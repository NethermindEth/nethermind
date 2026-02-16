// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nethermind.Benchmarks.Core;

[ShortRunJob]
[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class FastHashBenchmarks
{
    private byte[] _data = null!;

    [Params(16, 20, 32, 64, 128, 256, 512, 1024)]
    public int Size;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size];
        Random.Shared.NextBytes(_data);
    }

    [Benchmark(Baseline = true)]
    public int FastHash()
    {
        return ((ReadOnlySpan<byte>)_data).FastHash();
    }

    [Benchmark]
    public int FastHashAes()
    {
        ref byte start = ref MemoryMarshal.GetReference<byte>(_data);
        return SpanExtensions.FastHashAesX64(ref start, _data.Length, SpanExtensions.ComputeSeed(_data.Length));
    }

    [Benchmark]
    public int FastHashCrc()
    {
        ref byte start = ref MemoryMarshal.GetReference<byte>(_data);
        return SpanExtensions.FastHashCrc(ref start, _data.Length, SpanExtensions.ComputeSeed(_data.Length));
    }
}
