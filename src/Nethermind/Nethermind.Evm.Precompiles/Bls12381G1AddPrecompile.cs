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
public class Bls12381G1AddPrecompile : IPrecompile<Bls12381G1AddPrecompile>
{
    public static readonly Bls12381G1AddPrecompile Instance = new();

    private Bls12381G1AddPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x0b);

    public static string Name => "BLS12_G1ADD";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 375L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bls12381G1AddPrecompile++;

        const int expectedInputLength = 2 * Eip2537.LenG1;
        if (inputData.Length != expectedInputLength) return Errors.InvalidInputLength;

        G1 x = new(stackalloc long[G1.Sz]);
        G1 y = new(stackalloc long[G1.Sz]);
        Result result = x.TryDecodeRaw(inputData[..Eip2537.LenG1].Span) &&
                        y.TryDecodeRaw(inputData[Eip2537.LenG1..].Span);

        if (result)
        {
            // adding to infinity point has no effect
            if (x.IsInf()) return inputData[Eip2537.LenG1..].ToArray();
            if (y.IsInf()) return inputData[..Eip2537.LenG1].ToArray();

            G1 res = x.Add(y);
            return res.EncodeRaw();
        }

        return result.Error!;
    }
}
