// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class BlockAccessListsMessageSerializer : IZeroInnerMessageSerializer<BlockAccessListsMessage>
{
    public void Serialize(IByteBuffer byteBuffer, BlockAccessListsMessage message)
    {
        int length = GetLength(message, out int contentLength);
        byteBuffer.EnsureWritable(length);
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);

        foreach (byte[] bal in message.AccessLists.AsSpan())
        {
            stream.Encode(bal);
        }
    }

    public BlockAccessListsMessage Deserialize(IByteBuffer byteBuffer) => byteBuffer.DeserializeRlp(Deserialize);

    private static BlockAccessListsMessage Deserialize(ref Rlp.ValueDecoderContext ctx)
    {
        int length = ctx.ReadSequenceLength();
        int endPosition = ctx.Position + length;

        ArrayPoolList<byte[]> accessLists = new(16);
        while (ctx.Position < endPosition)
        {
            byte[] balBytes = ctx.DecodeByteArray();
            accessLists.Add(balBytes);
        }

        return new BlockAccessListsMessage(accessLists);
    }

    public int GetLength(BlockAccessListsMessage message, out int contentLength)
    {
        contentLength = 0;
        foreach (byte[] bal in message.AccessLists.AsSpan())
        {
            contentLength += Rlp.LengthOf(bal);
        }

        return Rlp.LengthOfSequence(contentLength);
    }
}
