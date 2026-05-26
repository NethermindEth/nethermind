// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Nethermind.JsonRpc;

internal static class JsonRpcArrayReader
{
    public static int CountItems(ReadOnlyMemory<byte> arrayBody)
    {
        Utf8JsonReader reader = new(arrayBody.Span, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            ThrowExpectedJsonArray();
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

        return ThrowIncompleteJsonArray();
    }

    public static bool TryReadNextItem(
        ReadOnlyMemory<byte> arrayBody,
        ref int offset,
        ref JsonReaderState readerState,
        ref bool started,
        out ReadOnlyMemory<byte> itemBody)
    {
        if (!TryReadNextItemRange(arrayBody, ref offset, ref readerState, ref started, out int itemStart, out int itemLength))
        {
            itemBody = default;
            return false;
        }

        itemBody = arrayBody.Slice(itemStart, itemLength);
        return true;
    }

    public static bool TryReadNextItemRange(
        ReadOnlyMemory<byte> arrayBody,
        ref int offset,
        ref JsonReaderState readerState,
        ref bool started,
        out int itemStart,
        out int itemLength)
    {
        itemStart = 0;
        itemLength = 0;
        Utf8JsonReader reader = new(arrayBody.Span[offset..], isFinalBlock: true, state: readerState);

        if (!started)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                ThrowExpectedJsonArray();
            }

            started = true;
        }

        if (!reader.Read())
        {
            ThrowIncompleteJsonArray();
        }

        if (reader.TokenType == JsonTokenType.EndArray)
        {
            offset += (int)reader.BytesConsumed;
            readerState = reader.CurrentState;
            return false;
        }

        itemStart = offset + (int)reader.TokenStartIndex;
        reader.Skip();
        int itemEnd = offset + (int)reader.BytesConsumed;
        itemLength = itemEnd - itemStart;
        offset = itemEnd;
        readerState = reader.CurrentState;
        return true;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowExpectedJsonArray() =>
        throw new JsonException("Expected JSON array.");

    [DoesNotReturn, StackTraceHidden]
    private static int ThrowIncompleteJsonArray() =>
        throw new JsonException("Incomplete JSON array.");
}
