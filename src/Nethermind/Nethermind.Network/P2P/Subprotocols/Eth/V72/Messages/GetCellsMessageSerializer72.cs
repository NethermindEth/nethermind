// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class GetCellsMessageSerializer72 : IZeroInnerMessageSerializer<GetCellsMessage72>
{
    // The wire limit remains permissive, but only the locally supported prefix is materialized.
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<GetCellsMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(GetCellsMessage72.Hashes));

    public void Serialize(IByteBuffer byteBuffer, GetCellsMessage72 message)
    {
        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);

        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(contentLength);
        writer.Encode(message.RequestId);

        int hashesLength = GetHashesContentLength(message.Hashes);
        writer.StartSequence(hashesLength);

        foreach (Hash256 hash in message.Hashes)
        {
            writer.Encode(hash);
        }

        writer.Encode(message.CellMask);
    }

    public GetCellsMessage72 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private static GetCellsMessage72 Deserialize(ref RlpReader ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + sequenceLength;
        long requestId = ctx.DecodeLong();

        int hashesCheckPosition = ctx.ReadSequenceLength() + ctx.Position;
        int hashCount = ctx.PeekNumberOfItemsRemaining(hashesCheckPosition, HashesRlpLimit.Limit + 1);
        ctx.GuardLimit(hashCount, HashesRlpLimit);
        int retainedHashCount = System.Math.Min(hashCount, Eth72ProtocolHandler.MaxCellsRequestHashes);
        Hash256[] hashes = new Hash256[retainedHashCount];
        for (int i = 0; i < retainedHashCount; i++)
        {
            hashes[i] = ctx.DecodeKeccak()
                ?? throw new RlpException($"Null transaction hash in {nameof(GetCellsMessage72)}.");
        }

        for (int i = retainedHashCount; i < hashCount; i++)
        {
            ctx.DecodeByteArraySpan(size: Hash256.Size);
        }

        ctx.Check(hashesCheckPosition);
        byte[] cellMask = ctx.DecodeByteArray(size: BlobCellMask.FixedByteLength);

        ctx.Check(checkPosition);
        return new GetCellsMessage72(requestId, hashes, cellMask);
    }

    public int GetLength(GetCellsMessage72 message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId)
            + Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes))
            + Rlp.LengthOf(message.CellMask);
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetHashesContentLength(Hash256[] hashes)
    {
        int contentLength = 0;
        for (int i = 0; i < hashes.Length; i++)
        {
            contentLength += Rlp.LengthOf(hashes[i]);
        }

        return contentLength;
    }
}
