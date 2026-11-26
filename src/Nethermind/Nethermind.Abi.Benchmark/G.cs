// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Nethermind.Int256;

namespace Nethermind.Abi.Benchmark;

/// <summary>The <c>g</c> example from the Solidity ABI specification's "Use of Dynamic Types" section.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class G
{
    private static readonly AbiSignature<(UInt256[][], string[])> Signature = new AbiSignature("g")
        .With(
            AbiType.Array(AbiType.Array(AbiType.UInt256)),
            AbiType.Array(AbiType.String));

    private static readonly (UInt256[][], string[]) Arguments = ([[1, 2], [3]], ["one", "two", "three"]);

    private static readonly byte[] Encoded = AbiCodec.Encode(Signature, Arguments);

    private static readonly byte[] Buffer = new byte[Encoded.Length];

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Encode")]
    public byte[] Encode() =>
        AbiCodec.Encode(Signature, Arguments);

    [Benchmark]
    [BenchmarkCategory("Encode")]
    public int EncodeIntoBuffer() =>
        AbiCodec.Encode(Signature, Arguments, Buffer);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Decode")]
    public (UInt256[][], string[]) Decode() =>
        AbiCodec.Decode(Signature, Encoded);
}
