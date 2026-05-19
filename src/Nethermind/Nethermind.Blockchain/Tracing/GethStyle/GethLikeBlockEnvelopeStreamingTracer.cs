// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Block tracer that writes Geth's per-tx envelope (<c>{"result": {…}, "txHash": "…"}</c>)
/// straight to the response <see cref="Utf8JsonWriter"/> as each transaction completes,
/// with the per-tx struct-log entries streamed through a <see cref="GethLikeTxStreamingMemoryTracer"/>.
/// Memory is bounded by one in-flight opcode entry regardless of block size.
/// </summary>
public sealed class GethLikeBlockEnvelopeStreamingTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxStreamingMemoryTracer>, IDisposable
{
    private readonly GethTraceOptions _options;
    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private bool _innerEnvelopeOpen;
    private Hash256? _currentTxHash;

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
        _innerEnvelopeOpen = true;
        _currentTxHash = tx?.Hash;
        return new GethLikeTxStreamingMemoryTracer(tx, _options, _writer, _pipeWriter, _cancellationToken);
    }

    protected override GethLikeTxTrace OnEnd(GethLikeTxStreamingMemoryTracer txTracer)
    {
        GethLikeTxTrace trace = txTracer.BuildResult();

        _writer.WriteEndArray();
        ForcedNumberConversion.WriteRawLong(_writer, "gas"u8, trace.Gas);
        _writer.WritePropertyName("failed"u8);
        _writer.WriteBooleanValue(trace.Failed);
        _writer.WritePropertyName("returnValue"u8);
        JsonSerializer.Serialize(_writer, trace.ReturnValue, EthereumJsonSerializer.JsonOptions);
        _writer.WriteEndObject();

        _writer.WritePropertyName("txHash"u8);
        JsonSerializer.Serialize(_writer, trace.TxHash, EthereumJsonSerializer.JsonOptions);
        _writer.WriteEndObject();
        _innerEnvelopeOpen = false;
        _currentTxHash = null;

        FlushPerTxEnvelope();
        return trace;
    }

    public void Dispose()
    {
        if (!_innerEnvelopeOpen) return;

        _writer.WriteEndArray();
        ForcedNumberConversion.WriteRawLong(_writer, "gas"u8, 0L);
        _writer.WritePropertyName("failed"u8);
        _writer.WriteBooleanValue(true);
        _writer.WritePropertyName("returnValue"u8);
        JsonSerializer.Serialize(_writer, Array.Empty<byte>(), EthereumJsonSerializer.JsonOptions);
        _writer.WriteEndObject();

        _writer.WritePropertyName("txHash"u8);
        JsonSerializer.Serialize(_writer, _currentTxHash, EthereumJsonSerializer.JsonOptions);
        _writer.WriteEndObject();
        _innerEnvelopeOpen = false;
        _currentTxHash = null;
    }

    private void FlushPerTxEnvelope()
    {
        if (_pipeWriter is null) return;
        _writer.Flush();
        _pipeWriter.FlushAsync(_cancellationToken).GetAwaiter().GetResult();
    }
}
