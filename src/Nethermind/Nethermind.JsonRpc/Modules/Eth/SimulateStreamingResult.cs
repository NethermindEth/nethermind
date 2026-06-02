// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// Streaming result for <c>eth_simulateV1</c> / <c>debug_simulateV1</c> / <c>trace_simulateV1</c>.
/// Each completed block is serialized to the response <see cref="Utf8JsonWriter"/> as soon as
/// the simulate engine finishes processing it; the cross-block list is never materialized.
/// <para>
/// <b>Warning — in-process consumers:</b> like the trace_*/debug_* streaming results, this
/// type's <see cref="IReadOnlyList{T}"/> implementation is intentionally empty. The HTTP/JSON-RPC
/// pipeline picks up the <see cref="IStreamableResult"/> interface and bypasses the buffered path
/// entirely. Programmatic in-process callers must use the buffered <see cref="SimulateTxExecutor{TTrace}"/>
/// branch and not this type.
/// </para>
/// </summary>
[JsonConverter(typeof(SimulateStreamingResultConverter<>))]
public sealed class SimulateStreamingResult<TTrace> : JsonStreamingResultBase, IReadOnlyList<SimulateBlockResult<TTrace>>
{
    private readonly Action<Utf8JsonWriter, PipeWriter?, CancellationToken> _runSimulation;

    public SimulateStreamingResult(
        Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runSimulation,
        CancellationTokenSource timeoutCts,
        ILogger logger)
        : base(timeoutCts, logger)
    {
        ArgumentNullException.ThrowIfNull(runSimulation);
        _runSimulation = runSimulation;
    }

    public int Count => 0;
    public SimulateBlockResult<TTrace> this[int index] => throw new InvalidOperationException(
        "SimulateStreamingResult is a streaming response object — its values are written directly to the JSON-RPC response and cannot be enumerated in-process. Use the buffered SimulateTxExecutor branch for programmatic callers.");
    public IEnumerator<SimulateBlockResult<TTrace>> GetEnumerator() => System.Linq.Enumerable.Empty<SimulateBlockResult<TTrace>>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    protected override void EmitContent(Utf8JsonWriter writer, PipeWriter? pipeWriter, CancellationToken cancellationToken)
        => SimulateEnvelopeWriter.EmitOuterArray(writer, pipeWriter, cancellationToken, _runSimulation);
}

internal sealed class SimulateStreamingResultConverter<TTrace> : JsonConverter<SimulateStreamingResult<TTrace>>
{
    public override SimulateStreamingResult<TTrace> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, SimulateStreamingResult<TTrace>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        value.WriteAsJson(writer);
    }
}
