// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// ReSharper disable InconsistentNaming

using BenchmarkDotNet.Attributes;
using Nethermind.Int256;
using Exp = Nethermind.Experimental.Abi;

namespace Nethermind.Abi.Benchmarks;

[MemoryDiagnoser]
public class G_Encode_Precomputed
{
    private readonly byte[] _g_encode_buffer = new byte[1024];

    private AbiSignature _g_signature = null!;
    private Exp.AbiSignature<(UInt256[][], string[])> _g_exp_signature = null!;
    private (UInt256[][], string[]) _g_arguments;
    private object[] _g_arguments_boxed = null!;

    [GlobalSetup]
    public void Setup()
    {
        _g_signature = new AbiSignature("g",
            new AbiArray(new AbiArray(AbiType.UInt256)),
            new AbiArray(AbiString.Instance));

        _g_exp_signature = new Exp.AbiSignature("g")
            .With(
                Exp.AbiType.Array(Exp.AbiType.Array(Exp.AbiType.UInt256)),
                Exp.AbiType.Array(Exp.AbiType.String));

        _g_arguments = ([[1, 2], [3]], ["one", "two", "three"]);
        _g_arguments_boxed = [_g_arguments.Item1, _g_arguments.Item2];
    }

    [Benchmark(Baseline = true)]
    public byte[] Current()
    {
        return AbiEncoder.Instance.Encode(AbiEncodingStyle.None, _g_signature, _g_arguments_boxed);
    }

    [Benchmark]
    public byte[] Experimental()
    {
        var w = new Exp.BinarySpanWriter(_g_encode_buffer);
        _g_exp_signature.Abi.Write(ref w, _g_arguments);

        return _g_encode_buffer;
    }
}
