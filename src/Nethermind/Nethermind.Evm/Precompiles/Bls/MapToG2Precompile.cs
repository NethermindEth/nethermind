// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;

using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class MapToG2Precompile : IPrecompile<MapToG2Precompile>
{
    public static MapToG2Precompile Instance = new MapToG2Precompile();

    private MapToG2Precompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x13);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 75000;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 2 * BlsParams.LenFp;
        if (inputData.Length != expectedInputLength)
        {
            return (Array.Empty<byte>(), false);
        }

        (byte[], bool) result;

        try
        {
            G2 res = new();
            res.map_to(inputData[16..64].ToArray(), inputData[(64+16)..].ToArray());
            if (res.is_inf())
            {
                result = (Enumerable.Repeat<byte>(0, 256).ToArray(), true);
            }
            else
            {
                result = (res.ToBytesUntrimmed(), true);
            }
        }
        catch (Exception)
        {
            result = (Array.Empty<byte>(), false);
        }

        return result;
    }
}
