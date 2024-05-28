// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

using G1 = Nethermind.Crypto.Bls.P1;
using Scalar = Nethermind.Crypto.Bls.Scalar;

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

    public static Address Address { get; } = Address.FromNumber(0x0d);

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

        for (int i = 0; i < (inputData.Length / ItemSize); i++)
        {
            int offset = i * ItemSize;
            if (!SubgroupChecks.G1IsInSubGroup(inputData.Span[offset..(offset + (2 * BlsParams.LenFp))]))
            {
                return (Array.Empty<byte>(), false);
            }
        }

        (byte[], bool) result;

        try
        {
            G1[] points = new G1[inputData.Length / ItemSize];
            Scalar[] scalars = new Scalar[inputData.Length / ItemSize];
            for (int i = 0; i < points.Length; i++)
            {
                int offset = i * ItemSize;
                points[i] = BlsExtensions.G1FromUntrimmed(inputData[offset..(offset + BlsParams.LenG1)]);
                scalars[i] = new(inputData[(offset + BlsParams.LenG1)..(offset + BlsParams.LenG1 + 32)].ToArray());

                if (!points[i].in_group() || !points[i].on_curve())
                {
                    return (Array.Empty<byte>(), false);
                }
            }
            G1 res = new();
            res.multi_mult(points, scalars);
            result = (res.ToBytesUntrimmed(), true);
        }
        catch (Exception)
        {
            result = (Array.Empty<byte>(), false);
        }

        return result;
    }
}
