// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Two override codes that every request sends, resolved and prepared for execution as a call does with each.
/// </summary>
/// <remarks>
/// The sizes are those of two codes seen meeting in one slot of the override code cache. With
/// <see cref="Colliding"/> the two hashes share their low 8 bits, so the codes meet in one slot of a direct-mapped
/// 256-slot table, and in one set of any smaller set-associative table indexed the same way.
/// </remarks>
[MemoryDiagnoser]
public class OverrideCodeCacheBenchmarks
{
    private byte[] _large = null!;
    private byte[] _small = null!;

    [Params(false, true)]
    public bool Colliding { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _large = RandomCode(15_132, seed: 1);
        int largeSlot = SlotOf(_large);
        for (int seed = 2; ; seed++)
        {
            byte[] small = RandomCode(1_201, seed);
            if ((SlotOf(small) == largeSlot) == Colliding)
            {
                _small = small;
                break;
            }
        }
    }

    [Benchmark]
    public int ResolveBoth() => Prepare(_large) + Prepare(_small);

    // The hash, the CodeInfo, and what the first execution of the code builds: its padded copy and jump destinations.
    private static int Prepare(byte[] code)
    {
        OverrideCodeCache.Resolve(code, out _, out CodeInfo codeInfo);
        return codeInfo.ExecutionCodeSpan.Length + codeInfo.JumpDestinationBitmap.Length;
    }

    private static int SlotOf(byte[] code) => ((ReadOnlySpan<byte>)code).FastHash() & 255;

    private static byte[] RandomCode(int length, int seed)
    {
        byte[] code = new byte[length];
        new Random(seed).NextBytes(code);
        return code;
    }
}
