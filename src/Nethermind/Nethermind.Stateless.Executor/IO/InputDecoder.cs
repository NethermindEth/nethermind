// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

internal static class InputDecoder
{
    internal static StatelessPayload Decode(ReadOnlySpan<byte> data)
    {
        ushort schemaVersion = BinaryPrimitives.ReadUInt16BigEndian(data);

        return schemaVersion switch
        {
            0 => DecodeV1<SszExecutionPayloadV3>(data[sizeof(ushort)..]),
            1 => DecodeV1<SszExecutionPayloadV4>(data[sizeof(ushort)..]),
            _ => throw new ArgumentException($"Unsupported schema version: {schemaVersion}", nameof(data))
        };
    }

    private static StatelessPayload DecodeV1<TExecutionPayload>(ReadOnlySpan<byte> data)
        where TExecutionPayload : SszExecutionPayloadV1, ISszExecutionPayloadFactory<TExecutionPayload>, ISszCodec<TExecutionPayload>, new()
    {
        StatelessInput<TExecutionPayload>.Decode(data, out StatelessInput<TExecutionPayload> input);
        NewPayloadRequest<TExecutionPayload>.Merkleize(input.NewPayloadRequest, out UInt256 root);

        return new(
            Block: input.NewPayloadRequest.ToBlock()!,
            Witness: input.Witness,
            ChainConfig: input.ChainConfig,
            PublicKeys: input.PublicKeys,
            NewPayloadRequestRoot: new Hash256(root.ToLittleEndian())
        );
    }
}
