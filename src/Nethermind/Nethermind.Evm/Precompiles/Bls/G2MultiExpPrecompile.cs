// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;

using G2 = Nethermind.Crypto.Bls.P2;
using Scalar = Nethermind.Crypto.Bls.Scalar;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G2MultiExpPrecompile : IPrecompile<G2MultiExpPrecompile>
{
    public static readonly G2MultiExpPrecompile Instance = new();

    private G2MultiExpPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x10);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 0L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 45000L * k * Discount.For(k) / 1000;
    }

    private const int ItemSize = 288;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % ItemSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        (byte[], bool) result;

        try
        {
            List<G2> points = [];
            List<Scalar> scalars = [];
            for (int i = 0; i < inputData.Length / ItemSize; i++)
            {
                int offset = i * ItemSize;
                G2? p = BlsExtensions.DecodeG2(inputData[offset..(offset + BlsParams.LenG2)].Span, out bool xInfinity);
                if (!p.HasValue)
                {
                    continue;
                }

                byte[] scalar = inputData[(offset + BlsParams.LenG2)..(offset + BlsParams.LenG2 + 32)].ToArray();
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
                return (Enumerable.Repeat<byte>(0, 256).ToArray(), true);
            }

            G2 res = new();
            res.MultiMult(points.ToArray(), scalars.ToArray());
            result = (res.Encode(), true);
        }
        catch (BlsExtensions.BlsPrecompileException)
        {
            return IPrecompile.Failure;
        }

        return result;
    }
}
