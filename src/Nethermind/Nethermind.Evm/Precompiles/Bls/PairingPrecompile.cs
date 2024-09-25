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

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 43000L * (inputData.Length / PairSize);

    public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % PairSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        G1 x = new(stackalloc long[G1.Sz]);
        G2 y = new(stackalloc long[G2.Sz]);

        var acc = GT.One();
        for (int i = 0; i < inputData.Length / PairSize; i++)
        {
            int offset = i * PairSize;

            if (!x.TryDecodeRaw(inputData[offset..(offset + BlsConst.LenG1)].Span) ||
                !y.TryDecodeRaw(inputData[(offset + BlsConst.LenG1)..(offset + PairSize)].Span))
            {
                return IPrecompile.Failure;
            }

            // x == inf || y == inf -> e(x, y) = 1
            if (x.IsInf() || y.IsInf())
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
}
