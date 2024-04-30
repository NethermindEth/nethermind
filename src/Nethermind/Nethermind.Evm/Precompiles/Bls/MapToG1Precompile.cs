// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using G1 = Nethermind.Crypto.Bls.P1;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class MapToG1Precompile : IPrecompile<MapToG1Precompile>
{
    public static MapToG1Precompile Instance = new MapToG1Precompile();

    private MapToG1Precompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x13);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return 5500L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 0L;
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        const int expectedInputLength = 64;
        if (inputData.Length != expectedInputLength)
        {
            return (Array.Empty<byte>(), false);
        }

        (byte[], bool) result;

        try
        {
            //todo: fix
            G1 res = new();
            // long[] fp = [
            //     (long)BinaryPrimitives.ReadUInt64BigEndian(inputData[0..8].Span),
            //     (long)BinaryPrimitives.ReadUInt64BigEndian(inputData[8..16].Span),
            //     (long)BinaryPrimitives.ReadUInt64BigEndian(inputData[16..24].Span),
            //     (long)BinaryPrimitives.ReadUInt64BigEndian(inputData[24..32].Span),
            //     (long)BinaryPrimitives.ReadUInt64BigEndian(inputData[32..40].Span),
            //     (long)BinaryPrimitives.ReadUInt64BigEndian(inputData[40..48].Span),
            //     (long)BinaryPrimitives.ReadUInt64BigEndian(inputData[48..56].Span),
            //     (long)BinaryPrimitives.ReadUInt64BigEndian(inputData[56..64].Span)
            // ];
            long[] fp = [
                // BinaryPrimitives.ReadInt64BigEndian(inputData[0..8].Span),
                // BinaryPrimitives.ReadInt64BigEndian(inputData[8..16].Span),
                BinaryPrimitives.ReadInt64BigEndian(inputData[16..24].Span),
                BinaryPrimitives.ReadInt64BigEndian(inputData[24..32].Span),
                BinaryPrimitives.ReadInt64BigEndian(inputData[32..40].Span),
                BinaryPrimitives.ReadInt64BigEndian(inputData[40..48].Span),
                BinaryPrimitives.ReadInt64BigEndian(inputData[48..56].Span),
                BinaryPrimitives.ReadInt64BigEndian(inputData[56..64].Span)
            ];
            res.map_to(fp);
            result = (res.ToBytesUntrimmed(), true);
        }
        catch (Exception)
        {
            result = (Array.Empty<byte>(), false);
        }

        return result;
    }
}
