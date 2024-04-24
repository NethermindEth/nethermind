// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G2MultiExpPrecompile : IPrecompile<G2MultiExpPrecompile>
{
    public static G2MultiExpPrecompile Instance = new G2MultiExpPrecompile();

    private G2MultiExpPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x11);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 55000L * k * Discount.For(k) / 1000;
    }

    private const int ItemSize = 288;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % ItemSize > 0 || inputData.Length == 0)
        {
            return (Array.Empty<byte>(), false);
        }

        (byte[], bool) result;

        try
        {
            G2 acc = G2.generator();
            for (int i = 0; i < inputData.Length / ItemSize; i++)
            {
                int offset = i * ItemSize;
                G2 x = BlsExtensions.G2FromUntrimmed(inputData[offset..(offset + BlsParams.LenG2)]);
                G2 res = x.mult(inputData[(offset + BlsParams.LenG2)..(offset + ItemSize)].ToArray().Reverse().ToArray());
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
