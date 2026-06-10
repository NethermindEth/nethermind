// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;

public class BlockAccessListsMessageSerializer : Eth66SerializerBase<BlockAccessListsMessage>
{
    private const int UnavailableBlockAccessListLength = 1;

    private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockAccessListsMessage>(
        GethSyncLimits.MaxBodyFetch, nameof(BlockAccessListsMessage.BlockAccessLists));

    protected override void SerializeInternal(IByteBuffer byteBuffer, BlockAccessListsMessage message)
    {
        IOwnedReadOnlyList<byte[]?> blockAccessLists = message.BlockAccessLists;
        RlpStream rlpStream = new NettyRlpStream(byteBuffer);
        rlpStream.StartSequence(GetBlockAccessListsContentLength(blockAccessLists));
        for (int i = 0; i < blockAccessLists.Count; i++)
        {
            byte[]? blockAccessListRlp = blockAccessLists[i];
            if (blockAccessListRlp is null)
            {
                rlpStream.WriteByte(Rlp.EmptyByteArrayByte);
            }
            else
            {
                rlpStream.Write(blockAccessListRlp);
            }
        }
    }

    public override BlockAccessListsMessage Deserialize(IByteBuffer byteBuffer)
    {
        using NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
        Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory);
        int startPosition = ctx.Position;
        ArrayPoolList<byte[]?>? blockAccessLists = null;

        try
        {
            int sequenceLength = ctx.ReadSequenceLength();
            int checkPosition = ctx.Position + sequenceLength;
            long requestId = ctx.DecodeLong();

            blockAccessLists = DecodeBlockAccessLists(ref ctx);
            ctx.Check(checkPosition);

            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + ctx.Position - startPosition);
            return new BlockAccessListsMessage(requestId, blockAccessLists);
        }
        catch
        {
            blockAccessLists?.Dispose();
            throw;
        }
    }

    protected override int GetLengthInternal(BlockAccessListsMessage message) =>
        Rlp.LengthOfSequence(GetBlockAccessListsContentLength(message.BlockAccessLists));

    protected override BlockAccessListsMessage DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId) =>
        new(requestId, DecodeBlockAccessLists(ref ctx));

    internal static int GetBlockAccessListEntryLength(byte[]? blockAccessListRlp) =>
        blockAccessListRlp is null ? UnavailableBlockAccessListLength : blockAccessListRlp.Length;

    private static int GetBlockAccessListsContentLength(IOwnedReadOnlyList<byte[]?> blockAccessLists)
    {
        int contentLength = 0;
        for (int i = 0; i < blockAccessLists.Count; i++)
        {
            contentLength += GetBlockAccessListEntryLength(blockAccessLists[i]);
        }

        return contentLength;
    }

    private static ArrayPoolList<byte[]?> DecodeBlockAccessLists(ref Rlp.ValueDecoderContext ctx)
    {
        int blockAccessListsContentLength = ctx.ReadSequenceLength();
        int checkPosition = ctx.Position + blockAccessListsContentLength;
        int entryCount = ctx.PeekNumberOfItemsRemaining(checkPosition, GethSyncLimits.MaxBodyFetch + 1);
        Rlp.GuardLimit(entryCount, blockAccessListsContentLength, RlpLimit);
        ArrayPoolList<byte[]?> blockAccessLists = new(entryCount);

        try
        {
            while (ctx.Position < checkPosition)
            {
                blockAccessLists.Add(DecodeBlockAccessListEntry(ref ctx));
            }

            ctx.Check(checkPosition);
            return blockAccessLists;
        }
        catch
        {
            blockAccessLists.Dispose();
            throw;
        }
    }

    private static byte[]? DecodeBlockAccessListEntry(ref Rlp.ValueDecoderContext ctx)
    {
        int length = ctx.PeekNextRlpLength();
        ReadOnlySpan<byte> blockAccessListRlp = ctx.Read(length);
        return length == UnavailableBlockAccessListLength && blockAccessListRlp[0] == Rlp.EmptyByteArrayByte
            ? null
            : blockAccessListRlp.ToArray();
    }
}
