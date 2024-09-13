// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
    public static readonly G1MultiExpPrecompile Instance = new();

    private G1MultiExpPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0d);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

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
            return IPrecompile.Failure;
        }

        (byte[], bool) result;

        try
        {
            List<G1> points = [];
            List<Scalar> scalars = [];
            for (int i = 0; i < inputData.Length / ItemSize; i++)
            {
                int offset = i * ItemSize;
                G1? p = BlsExtensions.DecodeG1(inputData[offset..(offset + BlsParams.LenG1)]);

                if (!p.HasValue)
                {
                    continue;
                }

                byte[] scalar = inputData[(offset + BlsParams.LenG1)..(offset + BlsParams.LenG1 + 32)].ToArray();
                if (scalar.All(x => x == 0))
                {
                    continue;
                }

                if (!p.Value.InGroup())
                {
                    return (Array.Empty<byte>(), false);
                }

                points.Add(p.Value);
                scalars.Add(new(scalar));
            }

            if (points.Count == 0)
            {
                return (Enumerable.Repeat<byte>(0, 128).ToArray(), true);
            }

            G1 res = new();
            res.MultiMult(points.ToArray(), scalars.ToArray());
            result = (res.Encode(), true);
        }
        catch (Exception)
        {
            result = ([], false);
        }

        return result;
    }
}
