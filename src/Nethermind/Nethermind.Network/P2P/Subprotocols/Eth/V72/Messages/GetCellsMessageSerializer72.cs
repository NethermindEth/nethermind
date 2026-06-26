// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class GetCellsMessageSerializer72 : IZeroInnerMessageSerializer<GetCellsMessage72>
{
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<GetCellsMessage72>(Eth72ProtocolHandler.MaxCellsResponseHashes, nameof(GetCellsMessage72.Hashes));

    public void Serialize(IByteBuffer byteBuffer, GetCellsMessage72 message)
    {
        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);

        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(contentLength);
        writer.Encode(message.RequestId);

        int payloadContentLength = GetPayloadContentLength(message);
        writer.StartSequence(payloadContentLength);

        int hashesLength = Rlp.LengthOf(message.Hashes);
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

        int payloadSequenceLength = ctx.ReadSequenceLength();
        int payloadCheckPosition = ctx.Position + payloadSequenceLength;
        using ArrayPoolList<Hash256> hashes = ctx.DecodeArrayPoolList(static (ref RlpReader c) => c.DecodeKeccak(), limit: HashesRlpLimit);
        byte[] cellMask = ctx.DecodeByteArray(size: BlobCellMask.FixedByteLength);

        ctx.Check(payloadCheckPosition);
        ctx.Check(checkPosition);
        return new GetCellsMessage72(requestId, hashes.AsSpan().ToArray(), cellMask);
    }

    public int GetLength(GetCellsMessage72 message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId) + Rlp.LengthOfSequence(GetPayloadContentLength(message));
        return Rlp.LengthOfSequence(contentLength);
    }

    private static int GetPayloadContentLength(GetCellsMessage72 message) =>
        Rlp.LengthOfSequence(Rlp.LengthOf(message.Hashes)) + Rlp.LengthOf(message.CellMask);
}
