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
public class MapToG2Precompile : IPrecompile<MapToG2Precompile>
{
    public static readonly MapToG2Precompile Instance = new();

    private MapToG2Precompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x13);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 75000;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 2 * BlsParams.LenFp;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        try
        {
            G2 res = new();
            if (!BlsExtensions.ValidUntrimmedFp(inputData.Span[..BlsParams.LenFp]) || !BlsExtensions.ValidUntrimmedFp(inputData.Span[BlsParams.LenFp..]))
            {
                return IPrecompile.Failure;
            }
            res.MapTo(inputData[BlsParams.LenFpPad..BlsParams.LenFp].ToArray(), inputData[(BlsParams.LenFp + BlsParams.LenFpPad)..].ToArray());
            return (res.Encode(), true);
        }
        catch (BlsExtensions.BlsPrecompileException)
        {
            return IPrecompile.Failure;
        }
    }
}
