// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;

namespace Nethermind.Consensus.Tracing;

/// <summary>The response a block traced in parallel is streamed into, as consecutive elements of one JSON array.</summary>
/// <remarks>A trace that can be serialized on its own is serialized by the worker that produced it, while the other
/// workers trace on, and only its bytes are appended in block order. A stream whose output depends on what was written
/// before it serializes its traces in block order instead.</remarks>
public interface IParallelTraceStream<TTrace>
{
    /// <summary>The writer options the elements are serialized ahead with, as the response writer would at the depth it
    /// writes them; null when the traces can only be serialized in block order. Read once, before the block starts.</summary>
    JsonWriterOptions? SerializeAheadOptions { get; }

    /// <summary>Writes <paramref name="traces"/> into <paramref name="writer"/> as the array elements
    /// <see cref="Write"/> would write, and releases them as <see cref="Write"/> does, even when it fails. Called
    /// concurrently, by the worker that traced them.</summary>
    void Serialize(IReadOnlyCollection<TTrace> traces, Utf8JsonWriter writer);

    /// <summary>Appends elements produced by <see cref="Serialize"/>, which may span several segments. Called in block
    /// order by one thread at a time, never with an empty sequence.</summary>
    void WriteSerialized(in ReadOnlySequence<byte> elements);

    /// <summary>Serializes <paramref name="traces"/> into the response and releases them, even when it fails. Called in
    /// block order by one thread at a time, when <see cref="SerializeAheadOptions"/> is null.</summary>
    void Write(IReadOnlyCollection<TTrace> traces);
}

/// <summary>Helpers for <see cref="IParallelTraceStream{TTrace}"/> implementations.</summary>
public static class ParallelTraceStream
{
    /// <summary>The options that serialize an element on its own exactly as <paramref name="writer"/> would at its
    /// current depth, or null when no such options exist: indentation depends on the depth the element is written at.</summary>
    public static JsonWriterOptions? SerializeAheadOptions(Utf8JsonWriter writer)
    {
        JsonWriterOptions options = writer.Options;
        int maxDepth = options.MaxDepth - writer.CurrentDepth;
        if (options.Indented || maxDepth <= 0) return null;

        return new JsonWriterOptions
        {
            Encoder = options.Encoder,
            MaxDepth = maxDepth,
            SkipValidation = true,
        };
    }
}
