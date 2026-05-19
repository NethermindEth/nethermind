// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

namespace Nethermind.JsonRpc;

internal static class JsonRpcArrayReader
{
    public static int CountItems(ReadOnlyMemory<byte> arrayBody)
    {
        Utf8JsonReader reader = new(arrayBody.Span, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected JSON array.");
        }

        int count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return count;
            }

            reader.Skip();
            count++;
        }

        throw new JsonException("Incomplete JSON array.");
    }

    public static bool TryReadNextItem(
        ReadOnlyMemory<byte> arrayBody,
        ref int offset,
        ref JsonReaderState readerState,
        ref bool started,
        out ReadOnlyMemory<byte> itemBody)
    {
        itemBody = default;
        Utf8JsonReader reader = new(arrayBody.Span[offset..], isFinalBlock: true, state: readerState);

        if (!started)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Expected JSON array.");
            }

            started = true;
        }

        if (!reader.Read())
        {
            throw new JsonException("Incomplete JSON array.");
        }

        if (reader.TokenType == JsonTokenType.EndArray)
        {
            offset += checked((int)reader.BytesConsumed);
            readerState = reader.CurrentState;
            return false;
        }

        int itemStart = checked(offset + (int)reader.TokenStartIndex);
        reader.Skip();
        int itemEnd = checked(offset + (int)reader.BytesConsumed);
        itemBody = arrayBody.Slice(itemStart, itemEnd - itemStart);
        offset = itemEnd;
        readerState = reader.CurrentState;
        return true;
    }
}
