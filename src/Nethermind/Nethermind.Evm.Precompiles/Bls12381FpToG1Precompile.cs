// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public class Bls12381FpToG1Precompile : IPrecompile<Bls12381FpToG1Precompile>
{
    public static readonly Bls12381FpToG1Precompile Instance = new();

    private Bls12381FpToG1Precompile() { }

    public static Address Address { get; } = Address.FromNumber(0x10);

    public static string Name => "BLS12_MAP_FP_TO_G1";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 5500L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bls12381FpToG1Precompile++;

        const int expectedInputLength = Eip2537.LenFp;
        if (inputData.Length != expectedInputLength) return Errors.InvalidInputLength;

        G1 res = new(stackalloc long[G1.Sz]);
        Result result = Eip2537.ValidRawFp(inputData.Span);
        if (!result) return result.Error!;

        // map field point to G1
        ReadOnlySpan<byte> fp = inputData[Eip2537.LenFpPad..Eip2537.LenFp].Span;
        res.MapTo(fp);

        return res.EncodeRaw();
    }
}
