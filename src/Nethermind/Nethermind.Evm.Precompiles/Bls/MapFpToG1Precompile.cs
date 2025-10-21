// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class MapFpToG1Precompile : IPrecompile<MapFpToG1Precompile>
{
    public static readonly MapFpToG1Precompile Instance = new();

    private MapFpToG1Precompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x10);

    public static string Name => "BLS12_MAP_FP_TO_G1";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 5500L;

    public Result<long> DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsMapFpToG1Precompile++;

        const int expectedInputLength = BlsConst.LenFp;
        if (inputData.Length != expectedInputLength) return Errors.InvalidInputLength;

        G1 res = new(stackalloc long[G1.Sz]);
        string? error = BlsExtensions.ValidRawFp(inputData.Span);
        if (error is not Errors.NoError) return error;

        // map field point to G1
        ReadOnlySpan<byte> fp = inputData[BlsConst.LenFpPad..BlsConst.LenFp].Span;
        res.MapTo(fp);

        return res.EncodeRaw();
    }
}
