// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Nethermind.Int256;
using Exp = Nethermind.Experimental.Abi;

namespace Nethermind.Abi.Benchmark;

/// <summary>The <c>g</c> example from the Solidity ABI specification's "Use of Dynamic Types" section.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class G
{
    private static readonly AbiSignature CurrentSignature = new("g",
        new AbiArray(new AbiArray(AbiType.UInt256)),
        new AbiArray(AbiString.Instance));

    private static readonly Exp.AbiSignature<(UInt256[][], string[])> ExperimentalSignature = new Exp.AbiSignature("g")
        .With(
            Exp.AbiType.Array(Exp.AbiType.Array(Exp.AbiType.UInt256)),
            Exp.AbiType.Array(Exp.AbiType.String));

    private static readonly (UInt256[][], string[]) Arguments = ([[1, 2], [3]], ["one", "two", "three"]);

    private static readonly object[] BoxedArguments = [Arguments.Item1, Arguments.Item2];

    private static readonly byte[] Encoded = Exp.AbiCodec.Encode(ExperimentalSignature, Arguments);

    private static readonly byte[] Buffer = new byte[Encoded.Length];

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Encode")]
    public byte[] CurrentEncode() =>
        AbiEncoder.Instance.Encode(AbiEncodingStyle.IncludeSignature, CurrentSignature, BoxedArguments);

    [Benchmark]
    [BenchmarkCategory("Encode")]
    public byte[] ExperimentalEncode() =>
        Exp.AbiCodec.Encode(ExperimentalSignature, Arguments);

    [Benchmark]
    [BenchmarkCategory("Encode")]
    public int ExperimentalEncodeIntoBuffer() =>
        Exp.AbiCodec.Encode(ExperimentalSignature, Arguments, Buffer);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Decode")]
    public object[] CurrentDecode() =>
        AbiEncoder.Instance.Decode(AbiEncodingStyle.IncludeSignature, CurrentSignature, Encoded);

    [Benchmark]
    [BenchmarkCategory("Decode")]
    public (UInt256[][], string[]) ExperimentalDecode() =>
        Exp.AbiCodec.Decode(ExperimentalSignature, Encoded);
}
