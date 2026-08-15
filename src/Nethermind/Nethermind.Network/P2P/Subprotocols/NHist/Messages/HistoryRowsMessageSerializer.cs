// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist.Messages;

/// <remarks>
/// History keys are [flat key | block], so every version of the same key repeats the flat key verbatim - 52 of a
/// storage row's 60 key bytes. Each entry therefore carries how many leading bytes it shares with the previous
/// entry's key instead of the bytes themselves; the reader rebuilds the full key. Entries stay whole in memory, so
/// this is confined to the wire.
/// </remarks>
public class HistoryRowsMessageSerializer : NHistSerializerBase<HistoryRowsMessage>
{
    protected override HistoryRowsMessage Deserialize(ref RlpReader ctx)
    {
        HistoryRowsMessage message = new();
        ctx.ReadSequenceLength();

        message.RequestId = ctx.DecodeLong();
        message.Refused = ctx.DecodeBool();

        byte[][] previousKey = [[]];
        message.Entries = ctx.DecodeArrayPoolList(
            (ref RlpReader c) => DecodeEntry(ref c, previousKey),
            limit: NHistMessageLimits.HistoryRowEntriesRlpLimit);

        byte[] cursor = ctx.DecodeByteArray(NHistMessageLimits.NextRowCursorRlpLimit);
        message.NextCursor = cursor.Length == 0 ? null : cursor;

        return message;
    }

    private static HistoryRowEntry DecodeEntry(ref RlpReader ctx, byte[][] previousKeyHolder)
    {
        ctx.ReadSequenceLength();
        int sharedPrefixLength = ctx.DecodeInt();
        byte[] keySuffix = ctx.DecodeByteArray();
        byte[] value = ctx.DecodeByteArray();

        byte[] previousKey = previousKeyHolder[0];
        if ((uint)sharedPrefixLength > (uint)previousKey.Length)
        {
            throw new RlpException(
                $"A history row claims to share {sharedPrefixLength} leading bytes with the previous key, which is only {previousKey.Length} bytes long.");
        }

        byte[] key;
        if (sharedPrefixLength == 0)
        {
            key = keySuffix;
        }
        else
        {
            key = new byte[sharedPrefixLength + keySuffix.Length];
            previousKey.AsSpan(0, sharedPrefixLength).CopyTo(key);
            keySuffix.CopyTo(key.AsSpan(sharedPrefixLength));
        }

        previousKeyHolder[0] = key;
        return new HistoryRowEntry(key, value);
    }

    public override void Serialize(IByteBuffer byteBuffer, HistoryRowsMessage message)
    {
        ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

        writer.Encode(message.RequestId);
        writer.Encode(message.Refused);

        IOwnedReadOnlyList<HistoryRowEntry> entries = message.Entries;
        writer.StartSequence(EntriesContentLength(entries));

        ReadOnlySpan<byte> previousKey = [];
        for (int i = 0; i < entries.Count; i++)
        {
            HistoryRowEntry entry = entries[i];
            int shared = SharedPrefixLength(previousKey, entry.Key);
            writer.StartSequence(EntryContentLength(shared, entry.Key, entry.Value.Span));
            writer.Encode(shared);
            writer.Encode(entry.Key.AsSpan(shared));
            writer.Encode(entry.Value);
            previousKey = entry.Key;
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
        ReadOnlySpan<byte> previousKey = [];
        for (int i = 0; i < entries.Count; i++)
        {
            HistoryRowEntry entry = entries[i];
            int shared = SharedPrefixLength(previousKey, entry.Key);
            length += Rlp.LengthOfSequence(EntryContentLength(shared, entry.Key, entry.Value.Span));
            previousKey = entry.Key;
        }

        return length;
    }

    private static int EntryContentLength(int sharedPrefixLength, byte[] key, ReadOnlySpan<byte> value) =>
        Rlp.LengthOf(sharedPrefixLength)
        + Rlp.LengthOf(key.AsSpan(sharedPrefixLength))
        + Rlp.LengthOf(value);

    private static int SharedPrefixLength(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current)
    {
        int max = Math.Min(previous.Length, current.Length);

        // A key must keep at least one byte of its own, so a repeated key still encodes as a distinct entry.
        if (max >= current.Length) max = current.Length - 1;

        int i = 0;
        while (i < max && previous[i] == current[i]) i++;
        return i;
    }
}
