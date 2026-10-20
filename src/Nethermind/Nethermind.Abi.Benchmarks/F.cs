// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Nethermind.Int256;
using Exp = Nethermind.Experimental.Abi;

namespace Nethermind.Abi.Benchmarks;

/// <summary>The <c>f</c> example from the Solidity ABI specification's "Use of Dynamic Types" section.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class F
{
    private static readonly AbiSignature CurrentSignature = new("f",
        AbiType.UInt256,
        new AbiArray(AbiType.UInt32),
        new AbiBytes(10),
        AbiType.DynamicBytes);

    private static readonly Exp.AbiSignature<(UInt256, uint[], byte[], byte[])> ExperimentalSignature = new Exp.AbiSignature("f")
        .With(
            Exp.AbiType.UInt256,
            Exp.AbiType.Array(Exp.AbiType.UInt32),
            Exp.AbiType.BytesM(10),
            Exp.AbiType.Bytes);

    private static readonly (UInt256, uint[], byte[], byte[]) Arguments =
        (0x123, [0x456, 0x789], "1234567890"u8.ToArray(), "Hello, world!"u8.ToArray());

    private static readonly object[] BoxedArguments = [Arguments.Item1, Arguments.Item2, Arguments.Item3, Arguments.Item4];

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
    public (UInt256, uint[], byte[], byte[]) ExperimentalDecode() =>
        Exp.AbiCodec.Decode(ExperimentalSignature, Encoded);
}
