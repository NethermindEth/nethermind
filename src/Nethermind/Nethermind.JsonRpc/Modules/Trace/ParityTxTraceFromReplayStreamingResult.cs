// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// Single-transaction streaming variant of <see cref="ParityTxTraceFromReplay"/> used by
/// <c>trace_call</c>, <c>trace_rawTransaction</c>, and <c>trace_replayTransaction</c>.
/// The trace delegate runs inside <see cref="WriteToAsync"/> (HTTP path) or
/// <see cref="WriteAsJson"/> (synchronous fallback) and writes a single JSON envelope
/// directly onto the response writer; the vmTrace value streams per-opcode through a
/// <see cref="StreamingParityLikeBlockTracer"/> so peak heap stays bounded by call depth.
/// </summary>
/// <remarks>
/// Inherits <see cref="ParityTxTraceFromReplay"/> so a <c>ResultWrapper&lt;ParityTxTraceFromReplay&gt;</c>
/// can return either the streaming or the buffered shape transparently — same pattern
/// used by <c>GethLikeTxTraceStreamingSingleResult</c> for <c>debug_traceTransaction</c>.
/// </remarks>
[JsonConverter(typeof(ParityTxTraceFromReplayStreamingResultConverter))]
public sealed class ParityTxTraceFromReplayStreamingResult : ParityTxTraceFromReplay, IStreamableResult, IDisposable
{
    private static readonly JsonWriterOptions StreamingWriterOptions = new() { SkipValidation = true };

    private readonly Action<Utf8JsonWriter, PipeWriter?, CancellationToken> _runTrace;
    private readonly Func<ParityTxTraceFromReplay>? _materializeForInProcess;
    private readonly CancellationTokenSource _timeoutCts;
    private readonly CancellationToken _timeoutToken;
    private readonly ILogger _logger;
    private ParityTxTraceFromReplay? _materialized;

    /// <param name="materializeForInProcess">
    /// Optional buffered fallback used by in-process consumers that read individual
    /// properties (e.g. test code asserting <c>.Data.Action</c>). HTTP/JSON-RPC clients go
    /// through <see cref="WriteToAsync"/> and never trigger materialisation.
    /// </param>
    public ParityTxTraceFromReplayStreamingResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runTrace,
        CancellationTokenSource timeoutCts,
        ILogger logger,
        Func<ParityTxTraceFromReplay>? materializeForInProcess = null)
    {
        ArgumentNullException.ThrowIfNull(runTrace);
        ArgumentNullException.ThrowIfNull(timeoutCts);

        _runTrace = runTrace;
        _materializeForInProcess = materializeForInProcess;
        _timeoutCts = timeoutCts;
        _timeoutToken = timeoutCts.Token;
        _logger = logger;
    }

    private ParityTxTraceFromReplay? Materialized =>
        _materialized ??= _materializeForInProcess?.Invoke();

    public override byte[]? Output { get => Materialized?.Output; set { } }
    public override Hash256? TransactionHash { get => Materialized?.TransactionHash; set { } }
    public override ParityVmTrace? VmTrace { get => Materialized?.VmTrace; set { } }
    public override ParityTraceAction? Action { get => Materialized?.Action; set { } }
    public override Dictionary<Address, ParityAccountStateChange>? StateChanges { get => Materialized?.StateChanges; set { } }

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(_timeoutToken, cancellationToken);
        CancellationToken combinedToken = linkedCts.Token;

        using Utf8JsonWriter jsonWriter = new(writer, StreamingWriterOptions);

        try
        {
            _runTrace(jsonWriter, writer, combinedToken);
            jsonWriter.Flush();
            await writer.FlushAsync(combinedToken);
        }
        catch (OperationCanceledException) when (combinedToken.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug("trace_* streaming cancelled mid-response; client receives a partial body.");
        }
    }

    internal void WriteAsJson(Utf8JsonWriter writer) => _runTrace(writer, null, _timeoutToken);

    public void Dispose() => _timeoutCts.Dispose();
}

internal sealed class ParityTxTraceFromReplayStreamingResultConverter : JsonConverter<ParityTxTraceFromReplayStreamingResult>
{
    public override ParityTxTraceFromReplayStreamingResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, ParityTxTraceFromReplayStreamingResult? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        value.WriteAsJson(writer);
    }
}
