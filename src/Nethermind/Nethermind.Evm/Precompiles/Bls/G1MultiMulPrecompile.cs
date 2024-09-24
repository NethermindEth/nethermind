// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G1MultiMulPrecompile : IPrecompile<G1MultiMulPrecompile>
{
    public static readonly G1MultiMulPrecompile Instance = new();

    private G1MultiMulPrecompile()
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

        try
        {
            int nItems = inputData.Length / ItemSize;

            bool onStack = nItems <= 7;
            Span<long> rawPoints = onStack ? stackalloc long[nItems * 18] : new long[nItems * 18];
            Span<byte> rawScalars = onStack ? stackalloc byte[nItems * 32] : new byte[nItems * 32];

            if (onStack)
            {
                rawPoints.Clear();
                rawScalars.Clear();
            }

            int npoints = 0;
            for (int i = 0; i < nItems; i++)
            {
                int offset = i * ItemSize;

                ReadOnlySpan<byte> rawPoint = inputData[offset..(offset + BlsParams.LenG1)].Span;
                ReadOnlySpan<byte> rawScalar = inputData[(offset + BlsParams.LenG1)..(offset + ItemSize)].Span;

                G1 p = new(rawPoints[(npoints * 18)..]);
                p.DecodeRaw(rawPoint);

                if (p.IsInf())
                {
                    continue;
                }

                if (!p.InGroup())
                {
                    return IPrecompile.Failure;
                }

                for (int j = 0; j < 32; j++)
                {
                    rawScalars[(npoints * 32) + j] = rawScalar[31 - j];
                }

                npoints++;
            }

            if (npoints == 0)
            {
                return (Enumerable.Repeat<byte>(0, 128).ToArray(), true);
            }

            G1 res = new G1().MultiMult(rawPoints, rawScalars, npoints);
            return (res.EncodeRaw(), true);
        }
        catch (BlsExtensions.BlsPrecompileException)
        {
            return IPrecompile.Failure;
        }
    }
}
