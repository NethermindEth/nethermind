// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using G2 = Nethermind.Crypto.Bls.P2;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class MapFp2ToG2Precompile : IPrecompile<MapFp2ToG2Precompile>
{
    public static readonly MapFp2ToG2Precompile Instance = new();

    private MapFp2ToG2Precompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x11);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 23800L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [SkipLocalsInit]
    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.BlsMapFp2ToG2Precompile++;

        const int expectedInputLength = 2 * BlsConst.LenFp;
        if (inputData.Length != expectedInputLength)
        {
            return IPrecompile.Failure;
        }

        G2 res = new(stackalloc long[G2.Sz]);
        if (!BlsExtensions.ValidRawFp(inputData.Span[..BlsConst.LenFp]) ||
            !BlsExtensions.ValidRawFp(inputData.Span[BlsConst.LenFp..]))
        {
            return IPrecompile.Failure;
        }

        // map field point to G2
        ReadOnlySpan<byte> fp0 = inputData[BlsConst.LenFpPad..BlsConst.LenFp].Span;
        ReadOnlySpan<byte> fp1 = inputData[(BlsConst.LenFp + BlsConst.LenFpPad)..].Span;
        res.MapTo(fp0, fp1);

        return (res.EncodeRaw(), true);
    }
}
