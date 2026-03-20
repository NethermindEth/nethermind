// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class BlockAccessListsMessageSerializer : Eth66SerializerBase<BlockAccessListsMessage>
{
    protected override void SerializeInternal(IByteBuffer byteBuffer, BlockAccessListsMessage message)
    {
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(GetBlockAccessListsContentLength(message.BlockAccessLists));

        foreach (byte[] bal in message.BlockAccessLists.AsSpan())
        {
            stream.Encode(bal);
        }
    }

    protected override BlockAccessListsMessage DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId)
    {
        int length = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + length;

        ArrayPoolList<byte[]> blockAccessLists = new(16);
        while (ctx.Position < endPosition)
        {
            byte[] balBytes = ctx.DecodeByteArray();
            blockAccessLists.Add(balBytes);
        }

        ctx.Check(endPosition);
        return new BlockAccessListsMessage(requestId, blockAccessLists);
    }

    protected override int GetLengthInternal(BlockAccessListsMessage message)
    {
        return Rlp.LengthOfSequence(GetBlockAccessListsContentLength(message.BlockAccessLists));
    }

    private static int GetBlockAccessListsContentLength(IOwnedReadOnlyList<byte[]> blockAccessLists)
    {
        int contentLength = 0;
        foreach (byte[] bal in blockAccessLists.AsSpan())
        {
            contentLength += Rlp.LengthOf(bal);
        }

        return contentLength;
    }
}
