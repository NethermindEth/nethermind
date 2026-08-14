// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Json;

/// <summary>Converts byte-array arrays as JSON arrays of hex strings.</summary>
/// <remarks>
/// Reuses <see cref="ByteArrayConverter"/> for each element. The initial capacity
/// is seeded from a per-instance EMA of observed counts, rounded up to the next
/// power of 2 and rented from <see cref="System.Buffers.ArrayPool{T}"/>. Subclasses override
/// <see cref="InitialEwma"/> for fields with known size distributions
/// (transactions vs. blob bundle vs. execution requests).
/// </remarks>
public class ByteArrayArrayConverter : JsonConverter<byte[][]>
{
    /// <summary>Initial EMA value; overridden per call-site to bias for the expected count.</summary>
    protected virtual int InitialEwma => 16;

    private int _ewma;

    public ByteArrayArrayConverter() => _ewma = InitialEwma;

    /// <inheritdoc/>
    public override byte[][]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray || !reader.Read()) ThrowJsonException();
        if (reader.TokenType == JsonTokenType.EndArray) return [];

        // Pow2 rounding of EMA matches ArrayPool's bucket sizes and gives natural slack
        // (anything in (N/2, N] rounds to N). Rent then snaps to its own bucket >= request.
        int seed = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, Volatile.Read(ref _ewma)));
        using ArrayPoolListRef<byte[]> values = new(seed);
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(ByteArrayConverter.Convert(ref reader)!);
            if (!reader.Read()) ThrowJsonException();
        }

        byte[][] result = values.ToArray();
        int count = values.Count;

        // EMA alpha = 1/8. Race-tolerant: Volatile is enough; lost updates only slow convergence.
        int prev = Volatile.Read(ref _ewma);
        Volatile.Write(ref _ewma, ((prev * 7) + count) >> 3);

        return result;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, byte[][] value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }

        writer.WriteStartArray();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is byte[] item) ByteArrayConverter.Convert(writer, item, skipLeadingZeros: false);
            else writer.WriteNullValue();
        }
        writer.WriteEndArray();
    }

    [DoesNotReturn]
    private static void ThrowJsonException() => throw new JsonException();
}

/// <summary>Seeded for the mainnet-typical <c>transactions</c> array (~100-600).</summary>
public sealed class TransactionsByteArrayArrayConverter : ByteArrayArrayConverter
{
    protected override int InitialEwma => 256;
}

/// <summary>Seeded for <c>blobsBundle.commitments/blobs/proofs</c> (post-BPO2 max = 21 -> pow2 32).</summary>
public sealed class BlobsBundleByteArrayArrayConverter : ByteArrayArrayConverter
{
    protected override int InitialEwma => 32;
}

/// <summary>Seeded for top-level <c>executionRequests</c> (typically 0-3).</summary>
public sealed class ExecutionRequestsByteArrayArrayConverter : ByteArrayArrayConverter
{
    protected override int InitialEwma => 4;
}
