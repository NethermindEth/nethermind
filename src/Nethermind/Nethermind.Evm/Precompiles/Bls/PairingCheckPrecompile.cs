// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;

using G1 = Nethermind.Crypto.Bls.P1;
using G2 = Nethermind.Crypto.Bls.P2;
using GT = Nethermind.Crypto.Bls.PT;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class PairingCheckPrecompile : IPrecompile<PairingCheckPrecompile>
{
    private const int PairSize = 384;
    public static readonly PairingCheckPrecompile Instance = new();

    private PairingCheckPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x11);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 65000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 43000L * (inputData.Length / PairSize);

    [SkipLocalsInit]
    public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsPairingCheckPrecompile++;

        if (inputData.Length % PairSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        G1 x = new(stackalloc long[G1.Sz]);
        G2 y = new(stackalloc long[G2.Sz]);

        using ArrayPoolList<long> buf = new(GT.Sz * 2, GT.Sz * 2);
        var acc = GT.One(buf.AsSpan());
        GT p = new(buf.AsSpan()[GT.Sz..]);

        for (int i = 0; i < inputData.Length / PairSize; i++)
        {
            int offset = i * PairSize;

            if (!x.TryDecodeRaw(inputData[offset..(offset + BlsConst.LenG1)].Span) ||
                !(BlsConst.DisableSubgroupChecks || x.InGroup()) ||
                !y.TryDecodeRaw(inputData[(offset + BlsConst.LenG1)..(offset + PairSize)].Span) ||
                !(BlsConst.DisableSubgroupChecks || y.InGroup()))
            {
                return IPrecompile.Failure;
            }

            // x == inf || y == inf -> e(x, y) = 1
            if (x.IsInf() || y.IsInf())
            {
                continue;
            }

            p.MillerLoop(y, x);
            acc.Mul(p);
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
