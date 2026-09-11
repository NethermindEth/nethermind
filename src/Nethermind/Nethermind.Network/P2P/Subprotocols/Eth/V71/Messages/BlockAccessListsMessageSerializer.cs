// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
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
        ByteBufferRlpWriter writer = new(byteBuffer);
        writer.StartSequence(GetBlockAccessListsContentLength(blockAccessLists));
        for (int i = 0; i < blockAccessLists.Count; i++)
        {
            WriteBlockAccessListEntry(ref writer, blockAccessLists[i]);
        }
    }

    public override BlockAccessListsMessage Deserialize(IByteBuffer byteBuffer)
    {
        RlpReader ctx = new(byteBuffer.AsSpan());
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

    protected override BlockAccessListsMessage DeserializeInternal(ref RlpReader ctx, long requestId) =>
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

    private static void WriteBlockAccessListEntry<TWriter>(ref TWriter writer, byte[]? blockAccessListRlp)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (blockAccessListRlp is null)
        {
            writer.WriteByte(Rlp.EmptyByteArrayByte);
        }
        else
        {
            writer.Write(blockAccessListRlp.AsSpan());
        }
    }

    private static ArrayPoolList<byte[]?> DecodeBlockAccessLists(ref RlpReader ctx)
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

    private static byte[]? DecodeBlockAccessListEntry(ref RlpReader ctx)
    {
        int length = ctx.PeekNextRlpLength();
        ReadOnlySpan<byte> blockAccessListRlp = ctx.Read(length);
        return length == UnavailableBlockAccessListLength && blockAccessListRlp[0] == Rlp.EmptyByteArrayByte
            ? null
            : blockAccessListRlp.ToArray();
    }
}
