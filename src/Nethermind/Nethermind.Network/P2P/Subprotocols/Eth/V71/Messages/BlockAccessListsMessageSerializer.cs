// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.FluentRlp;
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
        int contentLength = GetBlockAccessListsContentLength(blockAccessLists);
        int length = Rlp.LengthOfSequence(contentLength);
        RlpWriter writer = byteBuffer.StartRlpWrite(length);
        RlpSequence sequence = writer.StartSequence(contentLength);
        for (int i = 0; i < blockAccessLists.Count; i++)
        {
            WriteBlockAccessListEntry(ref writer, blockAccessLists[i]);
        }

        writer.EndSequence(sequence);
        byteBuffer.CompleteRlpWrite(in writer, length);
    }

    public override BlockAccessListsMessage Deserialize(IByteBuffer byteBuffer)
    {
        RlpReader reader = new(byteBuffer.AsSpan());
        ArrayPoolList<byte[]?>? blockAccessLists = null;

        try
        {
            RlpReader ctx = reader.EnterSequence(out int itemLength);
            long requestId = ctx.DecodeLong();

            blockAccessLists = DecodeBlockAccessLists(ref ctx);
            reader.ExitSequence(ctx, itemLength);

            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + reader.BytesRead);
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

    private static void WriteBlockAccessListEntry(ref RlpWriter writer, byte[]? blockAccessListRlp)
    {
        if (blockAccessListRlp is null)
        {
            writer.WriteByte(Rlp.EmptyByteArrayByte);
        }
        else
        {
            writer.WriteEncoded(blockAccessListRlp.AsSpan());
        }
    }

    private static ArrayPoolList<byte[]?> DecodeBlockAccessLists(ref RlpReader reader)
    {
        RlpReader ctx = reader.EnterSequence(out int itemLength);
        int entryCount = ctx.CountItems(RlpLimit);
        ArrayPoolList<byte[]?> blockAccessLists = new(entryCount);

        try
        {
            while (ctx.HasNext)
            {
                blockAccessLists.Add(DecodeBlockAccessListEntry(ref ctx));
            }

            reader.ExitSequence(ctx, itemLength);
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
        ReadOnlySpan<byte> blockAccessListRlp = ctx.ReadEncodedItem();
        return blockAccessListRlp.Length == UnavailableBlockAccessListLength && blockAccessListRlp[0] == Rlp.EmptyByteArrayByte
            ? null
            : blockAccessListRlp.ToArray();
    }
}
