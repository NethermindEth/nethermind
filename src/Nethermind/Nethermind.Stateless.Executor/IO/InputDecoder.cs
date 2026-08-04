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
    /// <summary>The schema revision selecting the SSZ <c>StatelessInput</c> payload encoding.</summary>
    internal const byte Revision1 = 0x01;

    internal static StatelessPayload Decode(ReadOnlySpan<byte> data)
    {
        ushort schemaId = BinaryPrimitives.ReadUInt16BigEndian(data);
        ProtocolFork fork = (ProtocolFork)(schemaId >> 8);
        byte revision = (byte)schemaId;
        ReadOnlySpan<byte> payload = data[sizeof(ushort)..];

        return (fork, revision) switch
        {
            (ProtocolFork.Amsterdam, Revision1) => DecodeRevision1<SszExecutionPayloadV4>(payload, fork),
            ( >= ProtocolFork.Cancun and < ProtocolFork.Amsterdam, Revision1) => DecodeRevision1<SszExecutionPayloadV3>(payload, fork),
            _ => throw new ArgumentException($"Unsupported schema id: 0x{schemaId:x4}", nameof(data))
        };
    }

    private static StatelessPayload DecodeRevision1<TExecutionPayload>(ReadOnlySpan<byte> data, ProtocolFork protocolFork)
        where TExecutionPayload : SszExecutionPayloadV1, ISszExecutionPayloadFactory<TExecutionPayload>, ISszCodec<TExecutionPayload>, new()
    {
        StatelessInput<TExecutionPayload>.Decode(data, out StatelessInput<TExecutionPayload> input);
        NewPayloadRequest<TExecutionPayload>.Merkleize(input.NewPayloadRequest, out UInt256 root);

        return new(
            Block: input.NewPayloadRequest.ToBlock(requestsEnabled: protocolFork >= ProtocolFork.Prague)!,
            Witness: input.Witness,
            ChainConfig: input.ChainConfig,
            PublicKeys: input.PublicKeys,
            NewPayloadRequestRoot: new Hash256(root.ToLittleEndian()),
            ProtocolFork: protocolFork
        );
    }
}
