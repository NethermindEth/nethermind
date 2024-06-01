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
public class G2MulPrecompile : IPrecompile<G2MulPrecompile>
{
    public static G2MulPrecompile Instance = new G2MulPrecompile();

    private G2MulPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0f);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 45000L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = BlsParams.LenG2 + BlsParams.LenFr;
        if (inputData.Length != expectedInputLength)
        {
            return (Array.Empty<byte>(), false);
        }

        (byte[], bool) result;

        try
        {
            G2? x = BlsExtensions.DecodeG2(inputData[..BlsParams.LenG2]);

            if (!x.HasValue)
            {
                // x == inf
                return (Enumerable.Repeat<byte>(0, 256).ToArray(), true);
            }

            if (!x.Value.in_group())
            {
                throw new Exception();
            }

            byte[] scalar = inputData[BlsParams.LenG2..].ToArray().Reverse().ToArray();

            if (scalar.All(x => x == 0))
            {
                return (Enumerable.Repeat<byte>(0, 256).ToArray(), true);
            }

            G2 res = x.Value.mult(scalar);
            result = (res.Encode(), true);
        }
        catch (Exception)
        {
            result = ([], false);
        }

        return result;
    }
}
