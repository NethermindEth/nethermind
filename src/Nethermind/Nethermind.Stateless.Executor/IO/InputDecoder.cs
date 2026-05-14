// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.Stateless.Execution.IO;

internal static class InputDecoder
{
    internal static StatelessPayload Decode(ReadOnlySpan<byte> data)
    {
        ushort schemaVersion = BinaryPrimitives.ReadUInt16BigEndian(data);

        return schemaVersion switch
        {
            0 => DecodeV0(data[sizeof(ushort)..]),
            1 => DecodeV1(data[sizeof(ushort)..]),
            _ => throw new ArgumentException($"Unsupported schema version: {schemaVersion}", nameof(data))
        };
    }

    private static StatelessPayload DecodeV0(ReadOnlySpan<byte> data)
    {
        StatelessInput<SszExecutionPayloadV3>.Decode(data, out StatelessInput<SszExecutionPayloadV3> input);

        return new(
            Block: input.NewPayloadRequest.ToBlock()!,
            Witness: input.Witness,
            ChainConfig: input.ChainConfig
        );
    }

    private static StatelessPayload DecodeV1(ReadOnlySpan<byte> data)
    {
        StatelessInput<SszExecutionPayloadV4>.Decode(data, out StatelessInput<SszExecutionPayloadV4> input);

        return new(
            Block: input.NewPayloadRequest.ToBlock()!,
            Witness: input.Witness,
            ChainConfig: input.ChainConfig
        );
    }
}
