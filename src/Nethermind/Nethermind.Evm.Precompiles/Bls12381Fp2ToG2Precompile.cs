// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public class Bls12381Fp2ToG2Precompile : IPrecompile<Bls12381Fp2ToG2Precompile>
{
    public static readonly Bls12381Fp2ToG2Precompile Instance = new();

    private Bls12381Fp2ToG2Precompile() { }

    public static Address Address { get; } = Address.FromNumber(0x11);

    public static string Name => "BLS12_MAP_FP2_TO_G2";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 23800L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bls12381Fp2ToG2Precompile++;

        const int expectedInputLength = 2 * Eip2537.LenFp;
        if (inputData.Length != expectedInputLength) return Errors.InvalidInputLength;

        G2 res = new(stackalloc long[G2.Sz]);
        Result result = Eip2537.ValidRawFp(inputData.Span[..Eip2537.LenFp]) &&
                        Eip2537.ValidRawFp(inputData.Span[Eip2537.LenFp..]);
        if (result)
        {
            // map field point to G2
            ReadOnlySpan<byte> fp0 = inputData[Eip2537.LenFpPad..Eip2537.LenFp].Span;
            ReadOnlySpan<byte> fp1 = inputData[(Eip2537.LenFp + Eip2537.LenFpPad)..].Span;
            res.MapTo(fp0, fp1);

            return res.EncodeRaw();
        }

        return result.Error!;
    }
}
