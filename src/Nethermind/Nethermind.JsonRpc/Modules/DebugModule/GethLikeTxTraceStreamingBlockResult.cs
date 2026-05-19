// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>
/// Streaming block-trace result with deferred execution. The supplied trace delegate runs
/// inside <see cref="StreamingResultBase.WriteToAsync"/> against a <see cref="Utf8JsonWriter"/>
/// wrapping the response <see cref="PipeWriter"/>; per-tx envelopes
/// <c>{"result": {…}, "txHash": "…"}</c> are emitted by
/// <see cref="GethLikeBlockEnvelopeStreamingTracer"/> as each transaction finishes,
/// with the inner struct-log entries streamed per opcode. Memory stays bounded by one
/// opcode entry regardless of block size.
/// </summary>
[JsonConverter(typeof(GethLikeTxTraceStreamingBlockResultConverter))]
public sealed class GethLikeTxTraceStreamingBlockResult : StreamingResultBase, IReadOnlyCollection<GethLikeTxTrace>
{
    private readonly Action<Utf8JsonWriter, PipeWriter?, CancellationToken> _runBlockTrace;

    public GethLikeTxTraceStreamingBlockResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runBlockTrace,
        CancellationTokenSource timeoutCts,
        ILogger logger)
        : base(timeoutCts, logger)
    {
        ArgumentNullException.ThrowIfNull(runBlockTrace);
        _runBlockTrace = runBlockTrace;
    }

    public int Count => 0;
    public IEnumerator<GethLikeTxTrace> GetEnumerator() => Enumerable.Empty<GethLikeTxTrace>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    protected override void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
    {
        Exception? failure = null;

        writer.WriteStartArray();

        try
        {
            _runBlockTrace(writer, pipeWriter, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            failure = ex;
        }

        writer.WriteEndArray();

        if (failure is not null && Logger.IsWarn)
        {
            Logger.Warn($"debug_traceBlock streaming failed mid-response: {failure}");
        }
    }
}

internal sealed class GethLikeTxTraceStreamingBlockResultConverter : JsonConverter<GethLikeTxTraceStreamingBlockResult>
{
    public override GethLikeTxTraceStreamingBlockResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, GethLikeTxTraceStreamingBlockResult? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        value.WriteAsJson(writer);
    }
}
