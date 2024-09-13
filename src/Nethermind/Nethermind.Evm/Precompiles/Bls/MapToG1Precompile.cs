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

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = BlsParams.LenFp;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        (byte[], bool) result;

        try
        {
            G1 res = new();
            if (!BlsExtensions.ValidFp(inputData.Span))
            {
                throw new Exception();
            }
            res.map_to(inputData[BlsParams.LenFpPad..BlsParams.LenFp].ToArray());
            result = (res.Encode(), true);
        }
        catch (Exception)
        {
            result = ([], false);
        }

        return result;
    }
}
