// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Block tracer that writes Geth's per-tx envelope (<c>{"result": {…}, "txHash": "…"}</c>)
/// straight to the response <see cref="Utf8JsonWriter"/> as each transaction completes,
/// with the per-tx struct-log entries streamed through a <see cref="GethLikeTxStreamingMemoryTracer"/>.
/// Memory is bounded by one in-flight opcode entry regardless of block size.
/// </summary>
public sealed class GethLikeBlockEnvelopeStreamingTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxStreamingMemoryTracer>
{
    private readonly GethTraceOptions _options;
    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;

    public GethLikeBlockEnvelopeStreamingTracer(
        GethTraceOptions options,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken)
        : base(options.TxHash)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _options = options;
        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
    }

    protected override GethLikeTxStreamingMemoryTracer OnStart(Transaction? tx)
    {
        _writer.WriteStartObject();
        _writer.WritePropertyName("result"u8);
        _writer.WriteStartObject();
        _writer.WritePropertyName("structLogs"u8);
        _writer.WriteStartArray();
        return new GethLikeTxStreamingMemoryTracer(tx, _options, _writer, _pipeWriter, _cancellationToken);
    }

    protected override GethLikeTxTrace OnEnd(GethLikeTxStreamingMemoryTracer txTracer)
    {
        GethLikeTxTrace trace = txTracer.BuildResult();

        _writer.WriteEndArray();
        WriteRawLong(_writer, "gas"u8, trace.Gas);
        _writer.WritePropertyName("failed"u8);
        _writer.WriteBooleanValue(trace.Failed);
        _writer.WritePropertyName("returnValue"u8);
        JsonSerializer.Serialize(_writer, trace.ReturnValue, EthereumJsonSerializer.JsonOptions);
        _writer.WriteEndObject();

        _writer.WritePropertyName("txHash"u8);
        JsonSerializer.Serialize(_writer, trace.TxHash, EthereumJsonSerializer.JsonOptions);
        _writer.WriteEndObject();

        FlushPerTxEnvelope();
        return trace;
    }

    private void FlushPerTxEnvelope()
    {
        if (_pipeWriter is null) return;
        _writer.Flush();
        _pipeWriter.FlushAsync(_cancellationToken).GetAwaiter().GetResult();
    }

    private static void WriteRawLong(Utf8JsonWriter writer, ReadOnlySpan<byte> name, long value)
    {
        writer.WritePropertyName(name);

        NumberConversion previous = ForcedNumberConversion.Value;
        ForcedNumberConversion.Value = NumberConversion.Raw;
        try
        {
            JsonSerializer.Serialize(writer, value, EthereumJsonSerializer.JsonOptions);
        }
        finally
        {
            ForcedNumberConversion.Value = previous;
        }
    }
}
