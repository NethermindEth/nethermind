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
public class G2MultiMulPrecompile : IPrecompile<G2MultiMulPrecompile>
{
    public static readonly G2MultiMulPrecompile Instance = new();

    private G2MultiMulPrecompile()
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

        try
        {
            int nItems = inputData.Length / ItemSize;

            bool onStack = nItems <= 4;
            Span<long> rawPoints = onStack ? stackalloc long[nItems * 36] : new long[nItems * 36];
            Span<byte> rawScalars = onStack ? stackalloc byte[nItems * 32] : new byte[nItems * 32];

            int npoints = 0;
            for (int i = 0; i < nItems; i++)
            {
                int offset = i * ItemSize;
                ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsParams.LenG2)].Span;
                ReadOnlySpan<byte> rawScalar = inputData[(offset + BlsParams.LenG2)..(offset + ItemSize)].Span;

                G2 p = BlsExtensions.DecodeG2(rawPoint, out bool isInfinity);

                if (isInfinity)
                {
                    continue;
                }

                if (!p.InGroup())
                {
                    return IPrecompile.Failure;
                }

                G2.Decode(
                    rawPoints[(npoints * 36)..],
                    rawPoint[BlsParams.LenFpPad..BlsParams.LenFp],
                    rawPoint[(BlsParams.LenFp + BlsParams.LenFpPad)..(2 * BlsParams.LenFp)],
                    rawPoint[(2 * BlsParams.LenFp + BlsParams.LenFpPad)..(3 * BlsParams.LenFp)],
                    rawPoint[(3 * BlsParams.LenFp + BlsParams.LenFpPad)..]
                );

                for (int j = 0; j < 32; j++)
                {
                    rawScalars[(npoints * 32) + j] = rawScalar[31 - j];
                }

                npoints++;
            }

            if (npoints == 0)
            {
                return (Enumerable.Repeat<byte>(0, 256).ToArray(), true);
            }

            G2 res = G2.Generator().MultiMult(rawPoints, rawScalars, npoints);

            return (res.Encode(), true);
        }
        catch (BlsExtensions.BlsPrecompileException)
        {
            return IPrecompile.Failure;
        }
    }
}
