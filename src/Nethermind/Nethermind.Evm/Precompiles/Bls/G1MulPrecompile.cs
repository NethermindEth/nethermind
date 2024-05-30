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
public class G1MulPrecompile : IPrecompile<G1MulPrecompile>
{
    public static G1MulPrecompile Instance = new G1MulPrecompile();

    private G1MulPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x0c);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 12000L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = BlsParams.LenG1 + BlsParams.LenFr;
        if (inputData.Length != expectedInputLength)
        {
            return (Array.Empty<byte>(), false);
        }

        (byte[], bool) result;

        try
        {
            G1? x = BlsExtensions.DecodeG1(inputData[..BlsParams.LenG1]);
            if (!x.HasValue)
            {
                // x == inf
                return (Enumerable.Repeat<byte>(0, 128).ToArray(), true);
            }

            if (!x.Value.in_group())
            {
                throw new Exception();
            }

            byte[] scalar = inputData[BlsParams.LenG1..].ToArray().Reverse().ToArray();

            if (scalar.All(x => x == 0))
            {
                return (Enumerable.Repeat<byte>(0, 128).ToArray(), true);
            }

            G1 res = x.Value.mult(scalar);
            result = (res.Encode(), true);
        }
        catch (Exception)
        {
            result = ([], false);
        }

        return result;
    }
}
