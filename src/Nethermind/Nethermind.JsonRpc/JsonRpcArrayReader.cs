// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Nethermind.JsonRpc;

internal static class JsonRpcArrayReader
{

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

        if (!TryReadItemStart(ref reader, ref started))
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

    /// <summary>Moves <paramref name="reader"/> onto the first token of the next array item.</summary>
    /// <param name="reader">Before the array's start while <paramref name="started"/> is <c>false</c>, otherwise on the previous item's last token.</param>
    /// <param name="started">Whether the array's start was already read; set once it is.</param>
    /// <returns><c>false</c> if <paramref name="reader"/> is on the array's end instead.</returns>
    public static bool TryReadItemStart(ref Utf8JsonReader reader, ref bool started)
    {
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

        return reader.TokenType != JsonTokenType.EndArray;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowExpectedJsonArray() =>
        throw new JsonException("Expected JSON array.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowIncompleteJsonArray() =>
        throw new JsonException("Incomplete JSON array.");
}
