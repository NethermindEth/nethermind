// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class G2AddPrecompile : IPrecompile<G2AddPrecompile>
{
    public static readonly G2AddPrecompile Instance = new();

    private G2AddPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0e);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 800L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 2 * BlsParams.LenG2;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        (byte[], bool) result;

        try
        {
            G2 x = BlsExtensions.DecodeG2(inputData[..BlsParams.LenG2].Span, out bool xInfinity);
            G2 y = BlsExtensions.DecodeG2(inputData[BlsParams.LenG2..].Span, out bool yInfinity);

            if (xInfinity)
            {
                // x == inf
                return (inputData[BlsParams.LenG2..], true);
            }

            if (yInfinity)
            {
                // y == inf
                return (inputData[..BlsParams.LenG2], true);
            }

            G2 res = x.Add(y);
            result = (res.Encode(), true);
        }
        catch (BlsExtensions.BlsPrecompileException)
        {
            return IPrecompile.Failure;
        }

        return result;
    }
}
