// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

internal static class InputDecoder
{
    /// <summary>The schema revision selecting the SSZ <c>StatelessInput</c> payload encoding.</summary>
    internal const byte Revision1 = 0x01;

    /// <summary>The schema id of a block on the chain's currently deployed fork.</summary>
    internal const ushort CurrentForkSchemaId = ((ushort)ProtocolFork.Current << 8) | Revision1;

    /// <summary>The schema id of an Amsterdam block.</summary>
    internal const ushort AmsterdamSchemaId = ((ushort)ProtocolFork.Amsterdam << 8) | Revision1;

    internal static StatelessPayload Decode(ReadOnlySpan<byte> data)
    {
        ushort schemaId = BinaryPrimitives.ReadUInt16BigEndian(data);
        ReadOnlySpan<byte> payload = data[sizeof(ushort)..];

        return schemaId switch
        {
            AmsterdamSchemaId => DecodeRevision1<SszExecutionPayloadAmsterdam>(payload, schemaId, ProtocolFork.Amsterdam),
            CurrentForkSchemaId => DecodeRevision1<SszExecutionPayload>(payload, schemaId, ProtocolFork.Current),
            _ => throw new ArgumentException($"Unsupported schema id: 0x{schemaId:x4}", nameof(data))
        };
    }

    private static StatelessPayload DecodeRevision1<TExecutionPayload>(
        ReadOnlySpan<byte> data, ushort schemaId, ProtocolFork protocolFork)
        where TExecutionPayload : SszExecutionPayload, ISszCodec<TExecutionPayload>, new()
    {
        StatelessInput<TExecutionPayload>.Decode(data, out StatelessInput<TExecutionPayload> input);
        NewPayloadRequest<TExecutionPayload>.Merkleize(input.NewPayloadRequest, out UInt256 root);

        TExecutionPayload executionPayload = input.NewPayloadRequest.ExecutionPayload;
        ForkActivation activation = new(executionPayload.BlockNumber, executionPayload.Timestamp);
        ISpecProvider specProvider = StatelessSpecProvider.Create(input.ChainId, protocolFork, activation);
        NewPayloadRequest<TExecutionPayload> newPayloadRequest = input.NewPayloadRequest;
        bool requestsEnabled = specProvider.GetSpec(activation).RequestsEnabled;

        return new(
            GetBlock: () => newPayloadRequest.ToBlock(requestsEnabled)!,
            Witness: input.Witness,
            ChainId: input.ChainId,
            SchemaId: schemaId,
            PublicKeys: input.PublicKeys,
            VersionedHashes: input.NewPayloadRequest.VersionedHashes,
            NewPayloadRequestRoot: new Hash256(root.ToLittleEndian()),
            SpecProvider: specProvider
        );
    }
}
