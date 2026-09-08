// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Block tracer that writes Geth's per-tx envelope (<c>{"result": {…}, "txHash": "…"}</c>)
/// straight to the response <see cref="Utf8JsonWriter"/> as each transaction completes,
/// with the per-tx struct-log entries streamed through a <see cref="GethLikeTxDirectStreamingTracer"/>.
/// Memory is bounded by one in-flight opcode regardless of block size; storage map is
/// mutated in place rather than cloned per opcode.
/// </summary>
public sealed class GethLikeBlockEnvelopeStreamingTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxDirectStreamingTracer>, IDisposable
{
    private readonly GethTraceOptions _options;
    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private bool _innerEnvelopeOpen;
    private bool _isDisposed;
    private Hash256? _currentTxHash;
    private GethLikeTxDirectStreamingTracer? _reusableTxTracer;

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

    protected override GethLikeTxDirectStreamingTracer OnStart(Transaction? tx)
    {
        _writer.WriteStartObject();
        _writer.WritePropertyName("result"u8);
        _writer.WriteStartObject();
        _writer.WritePropertyName("structLogs"u8);
        _writer.WriteStartArray();
        _innerEnvelopeOpen = true;
        _currentTxHash = tx?.Hash;
        if (_reusableTxTracer is null)
        {
            _reusableTxTracer = new GethLikeTxDirectStreamingTracer(tx, _options, _writer, _pipeWriter, _cancellationToken);
        }
        else
        {
            _reusableTxTracer.ResetForNextTx(tx);
        }
        return _reusableTxTracer;
    }

    protected override GethLikeTxTrace OnEnd(GethLikeTxDirectStreamingTracer txTracer)
    {
        GethLikeTxTrace trace = txTracer.BuildResult();

        _writer.WriteEndArray();
        _writer.WriteNumber("gas"u8, trace.Gas);
        _writer.WritePropertyName("failed"u8);
        _writer.WriteBooleanValue(trace.Failed);
        _writer.WritePropertyName("returnValue"u8);
        ByteArrayConverter.Convert(_writer, trace.ReturnValue, skipLeadingZeros: false);
        _writer.WriteEndObject();

        _writer.WritePropertyName("txHash"u8);
        WriteTxHashOrNull(trace.TxHash);
        _writer.WriteEndObject();
        _innerEnvelopeOpen = false;
        _currentTxHash = null;

        return trace;
    }

    protected override void AddTrace(GethLikeTxTrace trace) { }

    public override void EndBlockTrace()
    {
        _reusableTxTracer?.ReleaseResources();
        base.EndBlockTrace();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _reusableTxTracer?.ReleaseResources();
        DisposableExtensions.DisposeAndNull(ref _reusableTxTracer);

        if (!_innerEnvelopeOpen) return;

        Hash256? currentTxHash = _currentTxHash;
        _innerEnvelopeOpen = false;
        _currentTxHash = null;

        _writer.WriteEndArray();
        _writer.WriteNumber("gas"u8, 0L);
        _writer.WritePropertyName("failed"u8);
        _writer.WriteBooleanValue(true);
        _writer.WritePropertyName("returnValue"u8);
        ByteArrayConverter.Convert(_writer, [], skipLeadingZeros: false);
        _writer.WriteEndObject();

        _writer.WritePropertyName("txHash"u8);
        WriteTxHashOrNull(currentTxHash);
        _writer.WriteEndObject();
    }

    private void WriteTxHashOrNull(Hash256? hash)
    {
        if (hash is null) _writer.WriteNullValue();
        else HexWriter.WriteFixed32HexRawValue(_writer, hash.ValueHash256.Bytes);
    }
}
