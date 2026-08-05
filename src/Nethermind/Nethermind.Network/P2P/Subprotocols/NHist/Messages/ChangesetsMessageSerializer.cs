// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.State.SnapServer;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class ChangesetsMessageSerializer : NHistSerializerBase<ChangesetsMessage>
{
    protected override ChangesetsMessage Deserialize(ref RlpReader ctx)
    {
        ChangesetsMessage message = new();
        ctx.ReadSequenceLength();

        message.RequestId = ctx.DecodeLong();
        message.Chunks = ctx.DecodeArrayPoolList(static (ref RlpReader c) => DecodeChunk(ref c), limit: NHistMessageLimits.ChangesetChunksRlpLimit);

        return message;
    }

    private static ChangesetChunkEntry DecodeChunk(ref RlpReader ctx)
    {
        ctx.ReadSequenceLength();
        ulong block = ctx.DecodeULong();
        uint chunkIndex = (uint)ctx.DecodeULong();
        bool isLastChunkForBlock = ctx.DecodeBool();
        byte[] payload = ctx.DecodeByteArray();
        return new ChangesetChunkEntry(block, chunkIndex, isLastChunkForBlock, payload);
    }

    public override void Serialize(IByteBuffer byteBuffer, ChangesetsMessage message)
    {
        ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

        writer.Encode(message.RequestId);

        IOwnedReadOnlyList<ChangesetChunkEntry> chunks = message.Chunks;
        writer.StartSequence(ChunksContentLength(chunks));
        for (int i = 0; i < chunks.Count; i++)
        {
            ChangesetChunkEntry chunk = chunks[i];
            int chunkLength = Rlp.LengthOf(chunk.Block) + Rlp.LengthOf(chunk.ChunkIndex) + Rlp.LengthOf(chunk.IsLastChunkForBlock) + Rlp.LengthOf(chunk.Payload);
            writer.StartSequence(chunkLength);
            writer.Encode(chunk.Block);
            writer.Encode(chunk.ChunkIndex);
            writer.Encode(chunk.IsLastChunkForBlock);
            writer.Encode(chunk.Payload);
        }
    }

    public override int GetLength(ChangesetsMessage message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId);
        contentLength += Rlp.LengthOfSequence(ChunksContentLength(message.Chunks));

        return Rlp.LengthOfSequence(contentLength);
    }

    private static int ChunksContentLength(IOwnedReadOnlyList<ChangesetChunkEntry> chunks)
    {
        int length = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            ChangesetChunkEntry chunk = chunks[i];
            int chunkLength = Rlp.LengthOf(chunk.Block) + Rlp.LengthOf(chunk.ChunkIndex) + Rlp.LengthOf(chunk.IsLastChunkForBlock) + Rlp.LengthOf(chunk.Payload);
            length += Rlp.LengthOfSequence(chunkLength);
        }

        return length;
    }
}
