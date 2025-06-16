// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// ReSharper disable InconsistentNaming

using BenchmarkDotNet.Attributes;
using Nethermind.Int256;
using Exp = Nethermind.Experimental.Abi;

namespace Nethermind.Abi.Benchmarks;

[MemoryDiagnoser]
public class F_Encode
{
    [Benchmark(Baseline = true)]
    public byte[] Current()
    {
        AbiSignature signature = new AbiSignature("f",
            AbiType.UInt256,
            new AbiArray(AbiType.UInt32),
            new AbiBytes(10),
            AbiType.DynamicBytes);

        (UInt256, uint[], byte[], byte[]) arguments = (0x123, [0x456, 0x789], "1234567890"u8.ToArray(),
            "Hello, world!"u8.ToArray());
        object[] boxed = [arguments.Item1, arguments.Item2, arguments.Item3, arguments.Item4];

        byte[] encoded = AbiEncoder.Instance.Encode(AbiEncodingStyle.IncludeSignature, signature, boxed);

        return encoded;
    }

    [Benchmark]
    public byte[] Experimental()
    {
        var signature = new Exp.AbiSignature("f")
            .With(
                Exp.AbiType.UInt256,
                Exp.AbiType.Array(Exp.AbiType.UInt32),
                Exp.AbiType.BytesM(10),
                Exp.AbiType.Bytes);

        (UInt256, uint[], byte[], byte[]) arguments = (0x123, [0x456, 0x789], "1234567890"u8.ToArray(),
            "Hello, world!"u8.ToArray());
        byte[] encoded = Exp.AbiCodec.Encode(signature, arguments);

        return encoded;
    }
}
