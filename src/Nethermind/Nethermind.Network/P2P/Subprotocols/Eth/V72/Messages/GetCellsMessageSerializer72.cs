// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class GetCellsMessageSerializer72 : IZeroInnerMessageSerializer<GetCellsMessage72>
{
    private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<GetCellsMessage72>(NethermindSyncLimits.MaxHashesFetch, nameof(GetCellsMessage72.Hashes));

    public void Serialize(IByteBuffer byteBuffer, GetCellsMessage72 message)
    {
        int totalLength = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(totalLength);

        RlpStream rlpStream = new NettyRlpStream(byteBuffer);
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(message.RequestId);

        int payloadContentLength = GetPayloadContentLength(message);
        rlpStream.StartSequence(payloadContentLength);

        int hashesLength = GetHashesContentLength(message.Hashes);
        rlpStream.StartSequence(hashesLength);

        foreach (Hash256 hash in message.Hashes)
        {
            rlpStream.Encode(hash);
        }

        rlpStream.Encode(message.CellMask);
    }

    public GetCellsMessage72 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private static GetCellsMessage72 Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        int sequenceLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + sequenceLength;
        long requestId = ctx.DecodeLong();

        int payloadSequenceLength = ctx.ReadSequenceLength();
        int payloadCheckPosition = ctx.Position + payloadSequenceLength;
        using ArrayPoolList<Hash256> hashes = ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext c) => c.DecodeKeccak(), limit: HashesRlpLimit);
        byte[] cellMask = ctx.DecodeByteArraySpan().ToArray();
        if (cellMask.Length != BlobCellMask.FixedByteLength)
        {
            throw new RlpException($"Invalid cell mask length in {nameof(GetCellsMessage72)}: expected {BlobCellMask.FixedByteLength}, got {cellMask.Length}.");
        }

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
        Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes)) + Rlp.LengthOf(message.CellMask);

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
