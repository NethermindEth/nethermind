// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class BlockAccessListsMessageSerializer66(IZeroInnerMessageSerializer<BlockAccessListsMessage> innerSerializer)
    : IZeroInnerMessageSerializer<BlockAccessListsMessage66>
{
    private readonly IZeroInnerMessageSerializer<BlockAccessListsMessage> _innerSerializer = innerSerializer;

    public void Serialize(IByteBuffer byteBuffer, BlockAccessListsMessage66 message)
    {
        int length = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(length);

        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        stream.Encode(message.RequestId);
        _innerSerializer.Serialize(byteBuffer, message.EthMessage);
    }

    public BlockAccessListsMessage66 Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private BlockAccessListsMessage66 Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        ctx.ReadSequenceLength();
        long requestId = ctx.DecodeLong();
        BlockAccessListsMessage ethMessage = BlockAccessListsMessageSerializer.Deserialize(ref ctx);
        return new BlockAccessListsMessage66(requestId, ethMessage);
    }

    private static class BlockAccessListsMessageSerializer
    {
        public static BlockAccessListsMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
        {
            int length = ctx.ReadSequenceLength();
            int endPosition = ctx.Position + length;

            Core.Collections.ArrayPoolList<byte[]> accessLists = new(16);
            while (ctx.Position < endPosition)
            {
                byte[] balBytes = ctx.DecodeByteArray();
                accessLists.Add(balBytes);
            }

            return new BlockAccessListsMessage(accessLists);
        }
    }

    public int GetLength(BlockAccessListsMessage66 message, out int contentLength)
    {
        int innerLength = _innerSerializer.GetLength(message.EthMessage, out _);

        contentLength =
            Rlp.LengthOf(message.RequestId) +
            innerLength;

        return Rlp.LengthOfSequence(contentLength);
    }
}
