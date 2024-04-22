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

    public static Address Address { get; } = Address.FromNumber(0x0d);

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
            G1 x = BlsExtensions.G1FromUntrimmed(inputData[..BlsParams.LenG1]);
            G1 res = x.mult(inputData[BlsParams.LenG1..].ToArray().Reverse().ToArray());
            result = (res.ToBytesUntrimmed(), true);
        }
        catch (Exception)
        {
            result = (Array.Empty<byte>(), false);
        }

        return result;
    }
}
