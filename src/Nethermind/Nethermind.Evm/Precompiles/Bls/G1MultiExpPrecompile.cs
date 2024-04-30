// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G1MultiExpPrecompile : IPrecompile<G1MultiExpPrecompile>
{
    public static G1MultiExpPrecompile Instance = new G1MultiExpPrecompile();

    private G1MultiExpPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0e);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 12000L * k * Discount.For(k) / 1000;
    }

    private const int ItemSize = 160;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % ItemSize > 0 || inputData.Length == 0)
        {
            return (Array.Empty<byte>(), false);
        }

        (byte[], bool) result;

        try
        {
            G1 acc = new();
            for (int i = 0; i < inputData.Length / ItemSize; i++)
            {
                int offset = i * ItemSize;
                G1 x = BlsExtensions.G1FromUntrimmed(inputData[offset..(offset + BlsParams.LenG1)]);
                G1 res = x.mult(inputData[(offset + BlsParams.LenG1)..(offset + ItemSize)].ToArray().Reverse().ToArray());
                acc.add(res);
            }

            result = (acc.ToBytesUntrimmed(), true);
        }
        catch (Exception)
        {
            result = (Array.Empty<byte>(), false);
        }

        return result;
    }
}
