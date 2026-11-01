// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native;
using Nethermind.Serialization.Json;

namespace Nethermind.Consensus.Tracing;

/// <summary>Streams the traces of a block traced in parallel as its <c>{"result", "txHash"}</c> entries, as
/// <see cref="GethLikeBlockNativeStreamingTracer"/> writes them.</summary>
internal sealed class GethLikeTraceStream(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
    : IParallelTraceStream<GethLikeTxTrace>
{
    /// <summary>Flushed by the size of what is waiting, as the response writer flushes, rather than once per
    /// transaction: a flush blocks on the client while the transactions after it wait to be written.</summary>
    private const int FlushThresholdBytes = 16 * 1024;

    private readonly TraceStreamOutput _output = new(writer, pipeWriter, cancellationToken, FlushThresholdBytes);

    /// <inheritdoc />
    public JsonWriterOptions? SerializeAheadOptions => ParallelTraceStream.SerializeAheadOptions(writer);

    /// <inheritdoc />
    public void Serialize(IReadOnlyCollection<GethLikeTxTrace> traces, Utf8JsonWriter output)
    {
        int started = 0;
        try
        {
            foreach (GethLikeTxTrace trace in traces)
            {
                started++;
                using (trace)
                {
                    GethLikeTxTraceCollectionConverter.WriteEntry(output, trace, EthereumJsonSerializer.JsonOptions);
                }
            }
        }
        catch
        {
            DisposeAfter(traces, started);
            throw;
        }
    }

    /// <inheritdoc />
    public void WriteSerialized(ReadOnlySpan<byte> elements) => _output.WriteSerialized(elements);

    /// <inheritdoc />
    public void Write(IReadOnlyCollection<GethLikeTxTrace> traces)
    {
        int started = 0;
        try
        {
            foreach (GethLikeTxTrace trace in traces)
            {
                started++;
                GethLikeBlockNativeStreamingTracer.Write(writer, pipeWriter, trace, cancellationToken);
            }
        }
        catch
        {
            DisposeAfter(traces, started);
            throw;
        }
    }

    /// <summary>Disposes the traces after the first <paramref name="started"/>, which disposed themselves.</summary>
    private static void DisposeAfter(IReadOnlyCollection<GethLikeTxTrace> traces, int started)
    {
        int index = 0;
        foreach (GethLikeTxTrace trace in traces)
        {
            if (index++ >= started) trace.Dispose();
        }
    }
}
