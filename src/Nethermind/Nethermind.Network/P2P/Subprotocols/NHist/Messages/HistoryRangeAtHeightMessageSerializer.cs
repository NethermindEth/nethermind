// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class HistoryRangeAtHeightMessageSerializer : NHistSerializerBase<HistoryRangeAtHeightMessage>
{
    protected override HistoryRangeAtHeightMessage Deserialize(ref RlpReader ctx)
    {
        HistoryRangeAtHeightMessage message = new();
        ctx.ReadSequenceLength();

        message.RequestId = ctx.DecodeLong();
        message.Entries = ctx.DecodeArrayPoolList(static (ref RlpReader c) => DecodeEntry(ref c), limit: NHistMessageLimits.HistoryRangeEntriesRlpLimit);
        byte[] cursor = ctx.DecodeByteArray(NHistMessageLimits.CursorRlpLimit);
        message.NextCursor = cursor.Length == 0 ? null : cursor;

        return message;
    }

    private static HistoryRangeEntry DecodeEntry(ref RlpReader ctx)
    {
        ctx.ReadSequenceLength();
        byte[] key = ctx.DecodeByteArray();
        ulong block = ctx.DecodeULong();
        byte[] value = ctx.DecodeByteArray();
        bool isLiveFallback = ctx.DecodeBool();
        return new HistoryRangeEntry(key, block, value, isLiveFallback);
    }

    public override void Serialize(IByteBuffer byteBuffer, HistoryRangeAtHeightMessage message)
    {
        ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

        writer.Encode(message.RequestId);

        IOwnedReadOnlyList<HistoryRangeEntry> entries = message.Entries;
        writer.StartSequence(EntriesContentLength(entries));
        for (int i = 0; i < entries.Count; i++)
        {
            HistoryRangeEntry entry = entries[i];
            int entryLength = Rlp.LengthOf(entry.Key) + Rlp.LengthOf(entry.Block) + Rlp.LengthOf(entry.Value) + Rlp.LengthOf(entry.IsLiveFallback);
            writer.StartSequence(entryLength);
            writer.Encode(entry.Key);
            writer.Encode(entry.Block);
            writer.Encode(entry.Value);
            writer.Encode(entry.IsLiveFallback);
        }

        writer.Encode(message.NextCursor ?? []);
    }

    public override int GetLength(HistoryRangeAtHeightMessage message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId);
        contentLength += Rlp.LengthOfSequence(EntriesContentLength(message.Entries));
        contentLength += Rlp.LengthOf(message.NextCursor ?? []);

        return Rlp.LengthOfSequence(contentLength);
    }

    private static int EntriesContentLength(IOwnedReadOnlyList<HistoryRangeEntry> entries)
    {
        int length = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            HistoryRangeEntry entry = entries[i];
            int entryLength = Rlp.LengthOf(entry.Key) + Rlp.LengthOf(entry.Block) + Rlp.LengthOf(entry.Value) + Rlp.LengthOf(entry.IsLiveFallback);
            length += Rlp.LengthOfSequence(entryLength);
        }

        return length;
    }
}
