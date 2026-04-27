// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class BlockAccessListsMessageSerializer : Eth66SerializerBase<BlockAccessListsMessage>
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockAccessListsMessage>(
        GethSyncLimits.MaxBodyFetch, nameof(BlockAccessListsMessage.BlockAccessLists));

    protected override void SerializeInternal(IByteBuffer byteBuffer, BlockAccessListsMessage message)
    {
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(GetBlockAccessListsContentLength(message.BlockAccessLists));

        foreach (byte[]? bal in message.BlockAccessLists.AsSpan())
        {
            if (bal is not null)
            {
                stream.Encode(bal);
            }
            else
            {
                stream.EncodeEmptyByteArray();
            }
        }
    }

    protected override BlockAccessListsMessage DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId)
    {
        ArrayPoolList<byte[]?> blockAccessLists =
            ctx.DecodeArrayPoolList(static (ref Rlp.ValueDecoderContext nestedContext) => DecodeBlockAccessList(ref nestedContext), limit: RlpLimit);
        return new BlockAccessListsMessage(requestId, blockAccessLists);
    }

    protected override int GetLengthInternal(BlockAccessListsMessage message) =>
        Rlp.LengthOfSequence(GetBlockAccessListsContentLength(message.BlockAccessLists));

    private static int GetBlockAccessListsContentLength(IOwnedReadOnlyList<byte[]?> blockAccessLists)
    {
        int contentLength = 0;

        foreach (byte[]? bal in blockAccessLists.AsSpan())
        {
            contentLength += Rlp.LengthOf(bal);
        }

        return contentLength;
    }

    private static byte[]? DecodeBlockAccessList(ref Rlp.ValueDecoderContext ctx)
    {
        byte[] blockAccessList = ctx.DecodeByteArray();
        return blockAccessList.Length == 0 ? null : blockAccessList;
    }
}
