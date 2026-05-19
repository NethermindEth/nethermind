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
/// Struct-log tracer that serialises each completed entry to the supplied
/// <see cref="Utf8JsonWriter"/> the moment the next opcode arrives, instead of buffering
/// the whole array into <see cref="GethLikeTxTrace.Entries"/>. Memory stays bounded by
/// one opcode regardless of trace size.
/// </summary>
public sealed class GethLikeTxStreamingMemoryTracer : GethLikeTxMemoryTracer
{
    private const int DefaultFlushIntervalEntries = 256;

    private readonly Utf8JsonWriter _writer;
    private readonly PipeWriter? _pipeWriter;
    private readonly CancellationToken _cancellationToken;
    private readonly int _flushIntervalEntries;
    private int _entriesSinceLastFlush;

    /// <param name="pipeWriter">
    /// When non-null, every <paramref name="flushIntervalEntries"/> opcodes the tracer
    /// pushes accumulated bytes to the network via <see cref="PipeWriter.FlushAsync"/>
    /// (sync-over-async, so the EVM thread observes back-pressure). When null, no async
    /// flush is performed; the caller's <see cref="System.Buffers.IBufferWriter{T}"/>
    /// absorbs the entire output. The null path is intended for the synchronous-fallback
    /// JSON converter; production HTTP streaming should always supply the writer.
    /// </param>
    public GethLikeTxStreamingMemoryTracer(
        Transaction? transaction,
        GethTraceOptions options,
        Utf8JsonWriter writer,
        PipeWriter? pipeWriter,
        CancellationToken cancellationToken,
        int flushIntervalEntries = DefaultFlushIntervalEntries)
        : base(transaction, options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (flushIntervalEntries <= 0) throw new ArgumentOutOfRangeException(nameof(flushIntervalEntries));

        _writer = writer;
        _pipeWriter = pipeWriter;
        _cancellationToken = cancellationToken;
        _flushIntervalEntries = flushIntervalEntries;
    }

    protected override void AddTraceEntry(GethTxMemoryTraceEntry entry)
    {
        JsonSerializer.Serialize(_writer, entry, EthereumJsonSerializer.JsonOptions);

        if (_pipeWriter is null) return;
        if (++_entriesSinceLastFlush < _flushIntervalEntries) return;
        FlushToWire(_pipeWriter);
        _entriesSinceLastFlush = 0;
    }

    private void FlushToWire(PipeWriter pipeWriter)
    {
        _writer.Flush();
        pipeWriter.FlushAsync(_cancellationToken).GetAwaiter().GetResult();
    }
}
