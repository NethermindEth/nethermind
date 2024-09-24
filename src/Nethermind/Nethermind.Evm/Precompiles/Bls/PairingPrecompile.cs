// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

using G1 = Nethermind.Crypto.Bls.P1;
using G2 = Nethermind.Crypto.Bls.P2;
using GT = Nethermind.Crypto.Bls.PT;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class PairingPrecompile : IPrecompile<PairingPrecompile>
{
    private const int PairSize = 384;
    public static readonly PairingPrecompile Instance = new();

    private PairingPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x11);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 65000L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 43000L * (inputData.Length / PairSize);

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % PairSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        G1 x = new(stackalloc long[G1.Sz]);
        G2 y = new(stackalloc long[G2.Sz]);

        try
        {
            GT acc = GT.One();
            for (int i = 0; i < inputData.Length / PairSize; i++)
            {
                int offset = i * PairSize;

                x.DecodeRaw(inputData[offset..(offset + BlsParams.LenG1)].Span);
                bool xInfinity = x.IsInf();

                y.DecodeRaw(inputData[(offset + BlsParams.LenG1)..(offset + PairSize)].Span);
                bool yInfinity = y.IsInf();

                if ((!xInfinity && !x.InGroup()) || (!yInfinity && !y.InGroup()))
                {
                    return IPrecompile.Failure;
                }

                // x == inf || y == inf -> e(x, y) = 1
                if (xInfinity || yInfinity)
                {
                    continue;
                }

                acc.Mul(new GT(y, x));
            }

            bool verified = acc.FinalExp().IsOne();
            byte[] res = new byte[32];
            if (verified)
            {
                res[31] = 1;
            }

            return (res, true);
        }
        catch (BlsExtensions.BlsPrecompileException)
        {
            return IPrecompile.Failure;
        }
    }
}
