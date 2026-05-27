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
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Trace;

[JsonConverter(typeof(ParityTxTraceFromReplayStreamingResultConverter))]
public sealed class ParityTxTraceFromReplayStreamingResult : ParityTxTraceFromReplay, IStreamableResult, IDisposable
{
    private readonly DelegatingStreamingResult _inner;

    public Func<ParityTxTraceFromReplay?>? MaterializeForInProcess { get; init; }

    public ParityTxTraceFromReplayStreamingResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runExecution,
        CancellationTokenSource timeoutCts,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(runExecution);
        _inner = new DelegatingStreamingResult(runExecution, timeoutCts, logger);
    }

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        => _inner.WriteToAsync(writer, cancellationToken);

    internal void WriteAsJson(Utf8JsonWriter writer) => _inner.WriteAsJsonExternal(writer);

    public void Dispose() => _inner.Dispose();

    private bool _materialized;

    private void EnsureMaterialized()
    {
        if (_materialized) return;
        _materialized = true;
        if (MaterializeForInProcess is { } mat)
        {
            ParityTxTraceFromReplay? buffered = mat();
            if (buffered is not null)
            {
                base.Output = buffered.Output;
                base.TransactionHash = buffered.TransactionHash;
                base.VmTrace = buffered.VmTrace;
                base.Action = buffered.Action;
                base.StateChanges = buffered.StateChanges;
            }
        }
    }

    public override byte[]? Output { get { EnsureMaterialized(); return base.Output; } set => base.Output = value; }
    public override Hash256? TransactionHash { get { EnsureMaterialized(); return base.TransactionHash; } set => base.TransactionHash = value; }
    public override ParityVmTrace? VmTrace { get { EnsureMaterialized(); return base.VmTrace; } set => base.VmTrace = value; }
    public override ParityTraceAction? Action { get { EnsureMaterialized(); return base.Action; } set => base.Action = value; }
    public override Dictionary<Address, ParityAccountStateChange>? StateChanges { get { EnsureMaterialized(); return base.StateChanges; } set => base.StateChanges = value; }

    private sealed class DelegatingStreamingResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runExecution,
        CancellationTokenSource timeoutCts,
        ILogger logger) : StreamingResultBase(timeoutCts, logger)
    {
        protected override void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
            => runExecution(writer, pipeWriter, cancellationToken);

        internal void WriteAsJsonExternal(Utf8JsonWriter writer) => WriteAsJson(writer);
    }
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
