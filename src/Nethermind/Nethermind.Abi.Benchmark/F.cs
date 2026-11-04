// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Nethermind.Int256;

namespace Nethermind.Abi.Benchmark;

/// <summary>The <c>f</c> example from the Solidity ABI specification's "Use of Dynamic Types" section.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class F
{
    private static readonly AbiSignature<(UInt256, uint[], byte[], byte[])> Signature = new AbiSignature("f")
        .With(
            AbiType.UInt256,
            AbiType.Array(AbiType.UInt32),
            AbiType.BytesM(10),
            AbiType.Bytes);

    private static readonly (UInt256, uint[], byte[], byte[]) Arguments =
        (0x123, [0x456, 0x789], "1234567890"u8.ToArray(), "Hello, world!"u8.ToArray());

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
    public (UInt256, uint[], byte[], byte[]) Decode() =>
        AbiCodec.Decode(Signature, Encoded);
}
