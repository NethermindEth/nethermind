// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class MapToG1Precompile : IPrecompile<MapToG1Precompile>
{
    public static readonly MapToG1Precompile Instance = new MapToG1Precompile();

    private MapToG1Precompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x12);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 5500L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsMapFpToG1Precompile++;

        const int expectedInputLength = BlsConst.LenFp;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        G1 res = new G1(stackalloc long[G1.Sz]);
        if (!BlsExtensions.ValidRawFp(inputData.Span))
        {
            return IPrecompile.Failure;
        }
        res.MapTo(inputData[BlsConst.LenFpPad..BlsConst.LenFp].Span);
        return (res.EncodeRaw(), true);
    }
}
