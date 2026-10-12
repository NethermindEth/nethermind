// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Writes a <see cref="Witness"/> as the JSON-RPC <c>witness</c> RLP DATA string.</summary>
internal sealed class RlpExecutionWitnessJsonConverter : JsonConverter<Witness>
{
    private const int MaxSharedArrayLength = 1 << 20;
    private const int MaxLargePooledArrayLength = 1 << 25;
    private static readonly ArrayPool<byte> LargeArrayPool =
        ArrayPool<byte>.Create(maxArrayLength: MaxLargePooledArrayLength, maxArraysPerBucket: 2);

    public override Witness Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException($"{nameof(Witness)} owns pooled buffers and is only ever written.");

    public override void Write(Utf8JsonWriter writer, Witness value, JsonSerializerOptions options)
    {
        ReadOnlySpan<byte[]> headers = value.Headers.AsSpan();
        ReadOnlySpan<byte[]> codes = value.Codes.AsSpan();
        ReadOnlySpan<byte[]> state = value.State.AsSpan();

        int headersLength = RawContentLength(headers);
        int codesLength = ByteStringContentLength(codes);
        int stateLength = ByteStringContentLength(state);
        int contentLength = Rlp.LengthOfSequence(headersLength)
                            + Rlp.LengthOfSequence(codesLength)
                            + Rlp.LengthOfSequence(stateLength);
        int totalLength = Rlp.LengthOfSequence(contentLength);

        ArrayPool<byte> pool = totalLength > MaxSharedArrayLength ? LargeArrayPool : ArrayPool<byte>.Shared;
        using ArrayPoolSpan<byte> buffer = new(pool, totalLength);

        Span<byte> rlp = buffer;
        RlpWriter rlpWriter = new(rlp);

        rlpWriter.StartSequence(contentLength);
        WriteRaw(ref rlpWriter, headers, headersLength);
        WriteByteStrings(ref rlpWriter, codes, codesLength);
        WriteByteStrings(ref rlpWriter, state, stateLength);

        ByteArrayConverter.Convert(writer, rlp[..rlpWriter.Position], skipLeadingZeros: false);
    }

    private static void WriteRaw<TWriter>(ref TWriter writer, ReadOnlySpan<byte[]> items, int contentLength)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(contentLength);
        for (int i = 0; i < items.Length; i++)
            writer.Write(items[i]);
    }

    private static void WriteByteStrings<TWriter>(ref TWriter writer, ReadOnlySpan<byte[]> items, int contentLength)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(contentLength);
        for (int i = 0; i < items.Length; i++)
            writer.Encode(items[i]);
    }

    private static int RawContentLength(ReadOnlySpan<byte[]> items)
    {
        int length = 0;
        for (int i = 0; i < items.Length; i++)
            length += items[i].Length;
        return length;
    }

    private static int ByteStringContentLength(ReadOnlySpan<byte[]> items)
    {
        int length = 0;
        for (int i = 0; i < items.Length; i++)
            length += Rlp.LengthOf(items[i]);
        return length;
    }
}
