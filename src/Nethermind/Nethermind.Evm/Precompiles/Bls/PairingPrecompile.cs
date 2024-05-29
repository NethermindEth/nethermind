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

    private PairingPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x11);

    public static PairingPrecompile Instance = new PairingPrecompile();

    public long BaseGasCost(IReleaseSpec releaseSpec) => 65000L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 43000L * (inputData.Length / PairSize);
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % PairSize > 0 || inputData.Length == 0)
        {
            return (Array.Empty<byte>(), false);
        }

        (byte[], bool) result;

        try
        {
            GT acc = GT.one();
            for (int i = 0; i < inputData.Length / PairSize; i++)
            {
                int offset = i * PairSize;
                G1? x = BlsExtensions.G1FromUntrimmed(inputData[offset..(offset + BlsParams.LenG1)])!.Value;
                G2? y = BlsExtensions.G2FromUntrimmed(inputData[(offset + BlsParams.LenG1)..(offset + PairSize)]);

                if (!x.Value.on_curve() || !x.Value.in_group() || !y.Value.on_curve() || !y.Value.in_group())
                {
                    return (Array.Empty<byte>(), false);
                }

                acc.mul(new GT(y.Value, x.Value));
            }

            bool verified = acc.final_exp().is_one();
            byte[] res = new byte[32];
            if (verified)
            {
                res[31] = 1;
            }

            result = (res, true);
        }
        catch (Exception)
        {
            result = (Array.Empty<byte>(), false);
        }

        return result;
    }
}
