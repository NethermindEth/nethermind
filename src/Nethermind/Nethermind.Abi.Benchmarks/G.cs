// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// ReSharper disable InconsistentNaming

using BenchmarkDotNet.Attributes;
using Nethermind.Int256;
using Exp = Nethermind.Experimental.Abi;

namespace Nethermind.Abi.Benchmarks;

[MemoryDiagnoser]
public class G
{
    [Benchmark(Baseline = true)]
    public (UInt256[][], string[]) Current()
    {
        AbiSignature signature = new AbiSignature("g",
            new AbiArray(new AbiArray(AbiType.UInt256)),
            new AbiArray(AbiString.Instance));

        (UInt256[][], string[]) arguments = ([[1, 2], [3]], ["one", "two", "three"]);
        object[] boxed = [arguments.Item1, arguments.Item2];

        byte[] encoded = AbiEncoder.Instance.Encode(AbiEncodingStyle.IncludeSignature, signature, boxed);

        var decoded = AbiEncoder.Instance.Decode(AbiEncodingStyle.IncludeSignature, signature, encoded);

        return ((UInt256[][])decoded[0], (string[])decoded[1]);
    }

    [Benchmark]
    public (UInt256[][], string[]) Experimental()
    {
        Exp.AbiSignature<(UInt256[][], string[])> signature = new Exp.AbiSignature("g")
            .With(
                Exp.AbiType.Array(Exp.AbiType.Array(Exp.AbiType.UInt256)),
                Exp.AbiType.Array(Exp.AbiType.String));

        (UInt256[][], string[]) arguments = ([[1, 2], [3]], ["one", "two", "three"]);
        byte[] encoded = Exp.AbiCodec.Encode(signature, arguments);

        (UInt256[][], string[]) decoded = Exp.AbiCodec.Decode(signature, encoded);

        return decoded;
    }
}
