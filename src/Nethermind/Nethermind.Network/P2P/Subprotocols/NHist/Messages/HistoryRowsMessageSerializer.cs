// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

public class HistoryRowsMessageSerializer : NHistSerializerBase<HistoryRowsMessage>
{
    protected override HistoryRowsMessage Deserialize(ref RlpReader ctx)
    {
        HistoryRowsMessage message = new();
        ctx.ReadSequenceLength();

        message.RequestId = ctx.DecodeLong();
        message.Refused = ctx.DecodeBool();
        message.Entries = ctx.DecodeArrayPoolList(static (ref RlpReader c) => DecodeEntry(ref c), limit: NHistMessageLimits.HistoryRowEntriesRlpLimit);
        byte[] cursor = ctx.DecodeByteArray(NHistMessageLimits.NextRowCursorRlpLimit);
        message.NextCursor = cursor.Length == 0 ? null : cursor;

        return message;
    }

    private static HistoryRowEntry DecodeEntry(ref RlpReader ctx)
    {
        ctx.ReadSequenceLength();
        byte[] key = ctx.DecodeByteArray();
        byte[] value = ctx.DecodeByteArray();
        return new HistoryRowEntry(key, value);
    }

    public override void Serialize(IByteBuffer byteBuffer, HistoryRowsMessage message)
    {
        ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

        writer.Encode(message.RequestId);
        writer.Encode(message.Refused);

        IOwnedReadOnlyList<HistoryRowEntry> entries = message.Entries;
        writer.StartSequence(EntriesContentLength(entries));
        for (int i = 0; i < entries.Count; i++)
        {
            HistoryRowEntry entry = entries[i];
            int entryLength = Rlp.LengthOf(entry.Key) + Rlp.LengthOf(entry.Value);
            writer.StartSequence(entryLength);
            writer.Encode(entry.Key);
            writer.Encode(entry.Value);
        }

        writer.Encode(message.NextCursor ?? []);
    }

    public override int GetLength(HistoryRowsMessage message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.RequestId);
        contentLength += Rlp.LengthOf(message.Refused);
        contentLength += Rlp.LengthOfSequence(EntriesContentLength(message.Entries));
        contentLength += Rlp.LengthOf(message.NextCursor ?? []);

        return Rlp.LengthOfSequence(contentLength);
    }

    private static int EntriesContentLength(IOwnedReadOnlyList<HistoryRowEntry> entries)
    {
        int length = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            HistoryRowEntry entry = entries[i];
            int entryLength = Rlp.LengthOf(entry.Key) + Rlp.LengthOf(entry.Value);
            length += Rlp.LengthOfSequence(entryLength);
        }

        return length;
    }
}
