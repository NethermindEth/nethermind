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
    private static readonly RlpLimit RlpLimit = RlpLimit.For<BlockAccessListsMessage>(
        GethSyncLimits.MaxBodyFetch, nameof(BlockAccessListsMessage.BlockAccessLists));

    protected override void SerializeInternal(IByteBuffer byteBuffer, BlockAccessListsMessage message)
        => WriteBlockAccessLists(byteBuffer, message.BlockAccessLists);

    public override BlockAccessListsMessage Deserialize(IByteBuffer byteBuffer)
    {
        using NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
        Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, sliceMemory: true);
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
        GetBlockAccessListsLength(message.BlockAccessLists);

    protected override BlockAccessListsMessage DeserializeInternal(ref Rlp.ValueDecoderContext ctx, long requestId) =>
        new(requestId, DecodeBlockAccessLists(ref ctx));

    internal static int GetBlockAccessListEntryLength(byte[]? blockAccessListRlp) =>
        blockAccessListRlp is null ? Rlp.LengthOf(ReadOnlySpan<byte>.Empty) : blockAccessListRlp.Length;

    private static void WriteBlockAccessLists(IByteBuffer byteBuffer, IOwnedReadOnlyList<byte[]?> blockAccessLists)
    {
        NettyRlpStream rlpStream = new(byteBuffer);
        rlpStream.StartSequence(GetBlockAccessListsContentLength(blockAccessLists));
        for (int i = 0; i < blockAccessLists.Count; i++)
        {
            WriteBlockAccessListEntry(rlpStream, blockAccessLists[i]);
        }
    }

    private static void WriteBlockAccessListEntry(RlpStream stream, byte[]? blockAccessListRlp)
    {
        if (blockAccessListRlp is null)
        {
            stream.Encode(ReadOnlySpan<byte>.Empty);
            return;
        }

        stream.Write(blockAccessListRlp);
    }

    private static int GetBlockAccessListsLength(IOwnedReadOnlyList<byte[]?> blockAccessLists) =>
        Rlp.LengthOfSequence(GetBlockAccessListsContentLength(blockAccessLists));

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
        return length == 1 && blockAccessListRlp[0] == 0x80
            ? null
            : blockAccessListRlp.ToArray();
    }
}
