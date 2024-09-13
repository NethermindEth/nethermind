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

        (byte[], bool) result;

        try
        {
            GT acc = GT.One();
            for (int i = 0; i < inputData.Length / PairSize; i++)
            {
                int offset = i * PairSize;
                G1 x = BlsExtensions.DecodeG1(inputData[offset..(offset + BlsParams.LenG1)].Span, out bool xInfinity);
                G2 y = BlsExtensions.DecodeG2(inputData[(offset + BlsParams.LenG1)..(offset + PairSize)].Span, out bool yInfinity);

                if ((!xInfinity && !x.InGroup()) || (!yInfinity && !y.InGroup()))
                {
                    throw new Exception();
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

            result = (res, true);
        }
        catch (Exception)
        {
            result = ([], false);
        }

        return result;
    }
}
