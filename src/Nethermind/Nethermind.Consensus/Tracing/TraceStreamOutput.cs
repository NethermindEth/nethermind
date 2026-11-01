// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Consensus.Tracing;

/// <summary>The response a trace stream writes to: the JSON writer, and the pipe under it that is flushed once enough
/// bytes are waiting, so a response that is read slowly holds the writer only once per that many bytes.</summary>
/// <remarks>A mutable struct held in a field of its stream, so a request allocates nothing for it; never copy it.</remarks>
/// <param name="writer">Writes into <paramref name="output"/> when there is one.</param>
/// <param name="output">The pipe <paramref name="writer"/> writes into, or null when the response is not flushed.</param>
/// <param name="cancellationToken">Stops the writes; it carries the request's timeout.</param>
/// <param name="flushThresholdBytes">The bytes written between two flushes of <paramref name="output"/>.</param>
public struct TraceStreamOutput(Utf8JsonWriter writer, PipeWriter? output, CancellationToken cancellationToken, int flushThresholdBytes)
{
    /// <summary>The most bytes of an element that go through the writer; the rest reaches the pipe in its own
    /// segments, never as one contiguous span the size of the whole trace.</summary>
    internal const int ContiguousLimitBytes = 16 * 1024;

    private long _flushed = writer.BytesCommitted;
    private long _bypassed;

    /// <summary>Appends elements serialized ahead, with the separator the writer would put before them.</summary>
    /// <remarks>With a pipe, only the first bytes go through the writer, which adds the separator and records that an
    /// element was written; the rest is copied into the pipe segment by segment, and the pipe is flushed whenever the
    /// threshold is reached, also inside one long element. Without a pipe the writer's output is unknown, so the
    /// elements go through the writer whole.</remarks>
    public void WriteSerialized(in ReadOnlySequence<byte> elements)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (output is null)
        {
            writer.WriteRawValue(elements, skipInputValidation: true);
            return;
        }

        ReadOnlySpan<byte> first = elements.FirstSpan;
        int head = Math.Min(first.Length, ContiguousLimitBytes);
        writer.WriteRawValue(first[..head], skipInputValidation: true);
        if (head < elements.Length)
        {
            writer.Flush();
            ReadOnlySequence<byte> rest = elements.Slice(head);
            foreach (ReadOnlyMemory<byte> segment in rest)
            {
                output.Write(segment.Span);
                _bypassed += segment.Length;
                FlushIfDue();
            }
        }

        FlushIfDue();
    }

    /// <summary>Flushes the pipe once the bytes written since the last flush reach the threshold.</summary>
    public void FlushIfDue()
    {
        if (output is null || writer.BytesCommitted + writer.BytesPending + _bypassed - _flushed < flushThresholdBytes) return;

        writer.Flush();
        output.FlushAsync(cancellationToken).SafeWait();
        _flushed = writer.BytesCommitted + _bypassed;
    }
}
