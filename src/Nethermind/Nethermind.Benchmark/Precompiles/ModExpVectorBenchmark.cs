// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.Precompiles;

/// <summary>The MODEXP worst-case vectors from the execution-spec benchmark suite.</summary>
/// <remarks>
/// Each adversarial vector has a base that is either equal to the modulus or one above it, so the base
/// reduces to 0 or 1 and the exponentiation is constant. <see cref="Ordinary1024"/> is the control: a base
/// that reduces to neither, so the full ladder runs and the reduction is pure overhead.
/// </remarks>
[MemoryDiagnoser]
public class ModExpVectorBenchmark
{
    private const string F8 = "ffffffffffffffff";
    private const string F16 = F8 + F8;
    private const string F24 = F16 + F8;
    private const string F32 = F16 + F16;
    private const string F64 = F32 + F32;
    private const string F128 = F64 + F64;

    private static string Repeat(string hex, int times)
    {
        StringBuilder builder = new(hex.Length * times);
        for (int i = 0; i < times; i++)
        {
            builder.Append(hex);
        }
        return builder.ToString();
    }

    private static byte[] Encode(string @base, string exponent, string modulus)
    {
        byte[] baseBytes = Bytes.FromHexString(@base);
        byte[] exponentBytes = Bytes.FromHexString(exponent);
        byte[] modulusBytes = Bytes.FromHexString(modulus);

        byte[] input = new byte[96 + baseBytes.Length + exponentBytes.Length + modulusBytes.Length];
        input[31] = (byte)baseBytes.Length;
        input[63] = (byte)exponentBytes.Length;
        input[95] = (byte)modulusBytes.Length;
        baseBytes.CopyTo(input, 96);
        exponentBytes.CopyTo(input, 96 + baseBytes.Length);
        modulusBytes.CopyTo(input, 96 + baseBytes.Length + exponentBytes.Length);
        return input;
    }

    private byte[] _zkEvmWorstCase = null!;
    private byte[] _pawel1 = null!;
    private byte[] _pawel4 = null!;
    private byte[] _ordinary1024 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _zkEvmWorstCase = Encode(F32, F32, F32[..62] + "fe");
        _pawel1 = Encode(F8, F64, F8);
        _pawel4 = Encode(F32, F24, F32);
        _ordinary1024 = Encode(Repeat("03", 128), F128, F128);
    }

    [Benchmark]
    public int ZkEvmWorstCase() => ModExpPrecompile.Instance.Run(_zkEvmWorstCase, Osaka.Instance).Data!.Length;

    [Benchmark]
    public int Pawel1ExpHeavy() => ModExpPrecompile.Instance.Run(_pawel1, Osaka.Instance).Data!.Length;

    [Benchmark]
    public int Pawel4ExpHeavy() => ModExpPrecompile.Instance.Run(_pawel4, Osaka.Instance).Data!.Length;

    [Benchmark]
    public int Ordinary1024() => ModExpPrecompile.Instance.Run(_ordinary1024, Osaka.Instance).Data!.Length;
}
