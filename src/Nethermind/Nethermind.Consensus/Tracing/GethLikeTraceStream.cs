// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
/// <remarks>One instance per thread is reused from request to request: <see cref="Rent"/> points it at a response,
/// and <see cref="Return"/> lets go of the response once the block is streamed.</remarks>
internal sealed class GethLikeTraceStream : IParallelTraceStream<GethLikeTxTrace>
{
    /// <summary>Flushed by the size of what is waiting, as the response writer flushes, rather than once per
    /// transaction: a flush blocks on the client while the transactions after it wait to be written.</summary>
    private const int FlushThresholdBytes = 16 * 1024;

    [ThreadStatic] private static GethLikeTraceStream? _idle;

    private TraceStreamOutput _output;
    private bool _includeErrorResult;

    /// <summary>A stream into <paramref name="writer"/>; give it back with <see cref="Return"/>.</summary>
    public static GethLikeTraceStream Rent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken, bool includeErrorResult = false)
    {
        GethLikeTraceStream stream = _idle ?? new GethLikeTraceStream();
        _idle = null;
        stream._output = new TraceStreamOutput(writer, pipeWriter, cancellationToken, FlushThresholdBytes);
        stream._includeErrorResult = includeErrorResult;
        return stream;
    }

    /// <summary>Lets go of the response and keeps the instance for the next request on this thread.</summary>
    public void Return()
    {
        _output = default;
        _includeErrorResult = false;
        _idle = this;
    }

    /// <inheritdoc />
    public JsonWriterOptions? SerializeAheadOptions => ParallelTraceStream.SerializeAheadOptions(_output.Writer);

    /// <inheritdoc />
    public void Serialize(IReadOnlyCollection<GethLikeTxTrace> traces, Utf8JsonWriter output)
    {
        int started = 0;
        try
        {
            if (traces is IReadOnlyList<GethLikeTxTrace> list)
            {
                for (; started < list.Count; started++) WriteEntry(output, list[started]);
            }
            else
            {
                foreach (GethLikeTxTrace trace in traces)
                {
                    WriteEntry(output, trace);
                    started++;
                }
            }
        }
        catch
        {
            // The trace that failed disposed itself.
            DisposeAfter(traces, started + 1);
            throw;
        }
    }

    /// <inheritdoc />
    public void WriteSerialized(in ReadOnlySequence<byte> elements) => _output.WriteSerialized(elements);

    /// <inheritdoc />
    public void Write(IReadOnlyCollection<GethLikeTxTrace> traces)
    {
        int started = 0;
        try
        {
            if (traces is IReadOnlyList<GethLikeTxTrace> list)
            {
                for (; started < list.Count; started++) WriteInTurn(list[started]);
            }
            else
            {
                foreach (GethLikeTxTrace trace in traces)
                {
                    WriteInTurn(trace);
                    started++;
                }
            }
        }
        catch
        {
            // The trace that failed disposed itself.
            DisposeAfter(traces, started + 1);
            throw;
        }
    }

    private void WriteInTurn(GethLikeTxTrace trace) =>
        GethLikeBlockNativeStreamingTracer.Write(_output.Writer, _output.Output, trace, _output.CancellationToken, _includeErrorResult);

    private void WriteEntry(Utf8JsonWriter output, GethLikeTxTrace trace)
    {
        using (trace)
        {
            GethLikeTxTraceCollectionConverter.WriteEntry(output, trace, EthereumJsonSerializer.JsonOptions, _includeErrorResult);
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
