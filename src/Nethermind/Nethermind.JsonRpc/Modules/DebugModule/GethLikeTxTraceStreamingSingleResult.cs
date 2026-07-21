// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>
/// Streaming single-transaction trace result. The deferred trace delegate runs inside
/// <see cref="WriteToAsync"/> (production path) or <see cref="WriteAsJson"/> (synchronous
/// fallback used by the JSON converter) against a <see cref="Utf8JsonWriter"/> wrapping the
/// response <see cref="PipeWriter"/>; per-opcode entries leave the process before the next
/// opcode arrives. The trace function returns only the header fields (gas, failed, returnValue);
/// struct-log entries are streamed and never accumulated on the heap.
/// </summary>
[JsonConverter(typeof(GethLikeTxTraceStreamingSingleResultConverter))]
public sealed class GethLikeTxTraceStreamingSingleResult : GethLikeTxTrace, IStreamableResult
{
    private readonly Func<Utf8JsonWriter, PipeWriter?, CancellationToken, GethLikeTxTrace?> _runTrace;
    private readonly CancellationToken _timeoutToken;
    private readonly ILogger _logger;

    public GethLikeTxTraceStreamingSingleResult(
        Func<Utf8JsonWriter, PipeWriter?, CancellationToken, GethLikeTxTrace?> runTrace,
        CancellationTokenSource timeoutCts,
        ILogger logger)
        : base(timeoutCts)
    {
        ArgumentNullException.ThrowIfNull(runTrace);
        ArgumentNullException.ThrowIfNull(timeoutCts);

        _runTrace = runTrace;
        _timeoutToken = timeoutCts.Token;
        _logger = logger;
    }

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        StreamingResultBase.WriteJsonToAsync(_timeoutToken, _logger, writer, EmitContent, cancellationToken);

    /// <summary>
    /// Synchronous emission for the fallback path (test infrastructure and batch responses
    /// that bypass <see cref="IStreamableResult"/>). The entire trace is buffered into the
    /// caller's <see cref="System.Buffers.IBufferWriter{T}"/> behind <paramref name="writer"/>.
    /// </summary>
    internal void WriteAsJson(Utf8JsonWriter writer) => EmitContent(writer, null, _timeoutToken);

    private void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken) =>
        StructLogEnvelopeWriter.EmitTraceObject(writer, pipeWriter, cancellationToken, _runTrace, _logger);
}

internal sealed class GethLikeTxTraceStreamingSingleResultConverter : JsonConverter<GethLikeTxTraceStreamingSingleResult>
{
    public override GethLikeTxTraceStreamingSingleResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, GethLikeTxTraceStreamingSingleResult? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        value.WriteAsJson(writer);
    }
}
