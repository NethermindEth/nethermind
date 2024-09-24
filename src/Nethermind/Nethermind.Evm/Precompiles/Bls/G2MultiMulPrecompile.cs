// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Collections;
using System.Threading.Tasks;

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

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int k = inputData.Length / ItemSize;
        return 45000L * k * Discount.For(k) / 1000;
    }

    private const int ItemSize = 288;

    public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % ItemSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        try
        {
            int nItems = inputData.Length / ItemSize;

            long[] rawPoints = new long[nItems * 36];
            byte[] rawScalars = new byte[nItems * 32];
            using ArrayPoolList<int> pointDestinations = new(nItems);

            int npoints = 0;
            for (int i = 0; i < nItems; i++)
            {
                int offset = i * ItemSize;
                ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsParams.LenG2)].Span;

                // exclude infinity points
                int dest = rawPoint.ContainsAnyExcept((byte)0) ? npoints++ : -1;
                pointDestinations.Add(dest);
            }

            if (npoints == 0)
            {
                return (Enumerable.Repeat<byte>(0, 256).ToArray(), true);
            }

            bool fail = false;
            Parallel.ForEach(pointDestinations, (dest, state, i) =>
            {
                if (dest != -1)
                {
                    int offset = (int)i * ItemSize;
                    ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsParams.LenG2)].Span;
                    ReadOnlySpan<byte> rawScalar = inputData[(offset + BlsParams.LenG2)..(offset + ItemSize)].Span;

                    G2 p = new(rawPoints.AsSpan()[(dest * 36)..]);
                    p.DecodeRaw(rawPoint);

                    if (!p.InGroup())
                    {
                        fail = true;
                        state.Break();
                    }

                    for (int j = 0; j < 32; j++)
                    {
                        rawScalars[(dest * 32) + j] = rawScalar[31 - j];
                    }
                }
            });

            if (fail)
            {
                return IPrecompile.Failure;
            }

            G2 res = new G2().MultiMult(rawPoints, rawScalars, npoints);

            return (res.EncodeRaw(), true);
        }
        catch (BlsExtensions.BlsPrecompileException)
        {
            return IPrecompile.Failure;
        }
    }
}
