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

    public static Address Address { get; } = Address.FromNumber(0x10);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 55000L;
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
            G2 x = BlsExtensions.G2FromUntrimmed(inputData[..BlsParams.LenG2]);
            G2 res = x.mult(inputData[BlsParams.LenG2..].ToArray().Reverse().ToArray());
            result = (res.ToBytesUntrimmed(), true);
        }
        catch (Exception)
        {
            result = (Array.Empty<byte>(), false);
        }

        return result;
    }
}
