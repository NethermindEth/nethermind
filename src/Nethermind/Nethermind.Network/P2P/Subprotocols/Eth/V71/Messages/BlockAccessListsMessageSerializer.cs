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
        stream.StartSequence(GetAccessListsContentLength(message.AccessLists));

        foreach (byte[] bal in message.AccessLists.AsSpan())
        {
            stream.Encode(bal);
        }
    }

    protected override BlockAccessListsMessage DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId)
    {
        int length = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + length;

        ArrayPoolList<byte[]> accessLists = new(16);
        while (ctx.Position < endPosition)
        {
            byte[] balBytes = ctx.DecodeByteArray();
            accessLists.Add(balBytes);
        }

        ctx.Check(endPosition);
        return new BlockAccessListsMessage(requestId, accessLists);
    }

    protected override int GetLengthInternal(BlockAccessListsMessage message)
    {
        return Rlp.LengthOfSequence(GetAccessListsContentLength(message.AccessLists));
    }

    private static int GetAccessListsContentLength(IOwnedReadOnlyList<byte[]> accessLists)
    {
        int contentLength = 0;
        foreach (byte[] bal in accessLists.AsSpan())
        {
            contentLength += Rlp.LengthOf(bal);
        }

        return contentLength;
    }
}
