// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Data;

internal sealed class RlpExecutionWitnessJsonConverter : JsonConverter<Witness>
{
    public override Witness Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(RlpExecutionWitnessJsonConverter)} is serialize-only");

    public override void Write(Utf8JsonWriter writer, Witness value, JsonSerializerOptions options)
    {
        int headersContentLength = GetRawItemsLength(value.Headers);
        int codesContentLength = GetByteStringItemsLength(value.Codes);
        int stateContentLength = GetByteStringItemsLength(value.State);
        int contentLength = checked(
            Rlp.LengthOfSequence(headersContentLength)
            + Rlp.LengthOfSequence(codesContentLength)
            + Rlp.LengthOfSequence(stateContentLength));

        using ArrayPoolSpan<byte> encoded = new(Rlp.LengthOfSequence(contentLength));
        RlpWriter rlpWriter = new(encoded);
        rlpWriter.StartSequence(contentLength);
        WriteRawItems(ref rlpWriter, value.Headers, headersContentLength);
        WriteByteStringItems(ref rlpWriter, value.Codes, codesContentLength);
        WriteByteStringItems(ref rlpWriter, value.State, stateContentLength);

        ByteArrayConverter.Convert(writer, encoded, skipLeadingZeros: false);
    }

    private static int GetRawItemsLength(IOwnedReadOnlyList<byte[]> items)
    {
        int length = 0;
        ReadOnlySpan<byte[]> itemsSpan = items.AsSpan();
        for (int i = 0; i < itemsSpan.Length; i++)
        {
            length = checked(length + itemsSpan[i].Length);
        }

        return length;
    }

    private static int GetByteStringItemsLength(IOwnedReadOnlyList<byte[]> items)
    {
        int length = 0;
        ReadOnlySpan<byte[]> itemsSpan = items.AsSpan();
        for (int i = 0; i < itemsSpan.Length; i++)
        {
            length = checked(length + Rlp.LengthOf(itemsSpan[i]));
        }

        return length;
    }

    private static void WriteRawItems<TWriter>(
        ref TWriter writer,
        IOwnedReadOnlyList<byte[]> items,
        int contentLength)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(contentLength);
        ReadOnlySpan<byte[]> itemsSpan = items.AsSpan();
        for (int i = 0; i < itemsSpan.Length; i++)
        {
            writer.Write(itemsSpan[i]);
        }
    }

    private static void WriteByteStringItems<TWriter>(
        ref TWriter writer,
        IOwnedReadOnlyList<byte[]> items,
        int contentLength)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(contentLength);
        ReadOnlySpan<byte[]> itemsSpan = items.AsSpan();
        for (int i = 0; i < itemsSpan.Length; i++)
        {
            writer.Encode(itemsSpan[i]);
        }
    }
}
