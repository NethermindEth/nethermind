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
    private const ushort AmsterdamRevision1SchemaId = ((ushort)ProtocolFork.Amsterdam << 8) | 0x01;

    internal static StatelessPayload Decode(ReadOnlySpan<byte> data)
    {
        ushort schemaId = BinaryPrimitives.ReadUInt16BigEndian(data);

        return schemaId switch
        {
            AmsterdamRevision1SchemaId => DecodeRevision1<SszExecutionPayloadV4>(
                data[sizeof(ushort)..],
                ProtocolFork.Amsterdam),
            _ => throw new ArgumentException($"Unsupported schema id: 0x{schemaId:x4}", nameof(data))
        };
    }

    private static StatelessPayload DecodeRevision1<TExecutionPayload>(ReadOnlySpan<byte> data, ProtocolFork protocolFork)
        where TExecutionPayload : SszExecutionPayloadV1, ISszExecutionPayloadFactory<TExecutionPayload>, ISszCodec<TExecutionPayload>, new()
    {
        StatelessInput<TExecutionPayload>.Decode(data, out StatelessInput<TExecutionPayload> input);
        NewPayloadRequest<TExecutionPayload>.Merkleize(input.NewPayloadRequest, out UInt256 root);

        return new(
            Block: input.NewPayloadRequest.ToBlock()!,
            Witness: input.Witness,
            ChainConfig: input.ChainConfig,
            PublicKeys: input.PublicKeys,
            NewPayloadRequestRoot: new Hash256(root.ToLittleEndian()),
            ProtocolFork: protocolFork
        );
    }
}
