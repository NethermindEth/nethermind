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
/// <param name="writer">Writes into <paramref name="output"/> when there is one.</param>
/// <param name="output">The pipe <paramref name="writer"/> writes into, or null when the response is not flushed.</param>
public sealed class TraceStreamOutput(Utf8JsonWriter writer, PipeWriter? output, CancellationToken cancellationToken, int flushThresholdBytes)
{
    /// <summary>Elements longer than this reach the pipe in its own segments rather than as one contiguous span the
    /// size of the whole trace.</summary>
    internal const int ContiguousLimitBytes = 16 * 1024;

    private long _flushed = writer.BytesCommitted;
    private long _bypassed;

    /// <summary>Appends elements serialized ahead, with the separator the writer would put before them.</summary>
    public void WriteSerialized(ReadOnlySpan<byte> elements)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (output is null || elements.Length <= ContiguousLimitBytes)
        {
            writer.WriteRawValue(elements, skipInputValidation: true);
        }
        else
        {
            // The writer writes the separator and records that an element was written; the rest goes past it.
            writer.WriteRawValue(elements[..ContiguousLimitBytes], skipInputValidation: true);
            writer.Flush();
            output.Write(elements[ContiguousLimitBytes..]);
            _bypassed += elements.Length - ContiguousLimitBytes;
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
