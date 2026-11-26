// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

/// <summary>One response's <see cref="IStreamedResultObserver"/> and what its result ended with.</summary>
internal sealed class StreamedResultObservation(IStreamedResultObserver observer, ILogger logger)
{
    private bool _resultComplete;
    private bool _observerFailed;
    private bool _flushStopped;

    /// <summary>A writer that forwards to <paramref name="writer"/> and reports what is written to the observer.</summary>
    /// <remarks>A counting writer stays a counting writer, since results such as the logs stream size themselves by it.</remarks>
    public PipeWriter Wrap(PipeWriter writer) => writer is CountingWriter counting
        ? new ObservingCountingWriter(counting, this)
        : new ObservingPipeWriter(writer, this);

    public void ResultWritten(StreamableResultStatus? status) => _resultComplete = status is null or StreamableResultStatus.Complete;

    public void Complete(bool written)
    {
        try
        {
            observer.OnCompleted(written && _resultComplete && !_observerFailed && !_flushStopped);
        }
        catch (Exception e)
        {
            if (logger.IsError) logger.Error($"{observer.GetType().Name} failed to complete a streamed result", e);
        }
    }

    /// <summary>Notes a flush that stops the result early.</summary>
    /// <remarks>
    /// A result without a status returns normally when a flush reports the reader completed or the flush cancelled,
    /// for example when an HTTP client disconnects, so such a result can be cut short without an error.
    /// </remarks>
    private ValueTask<FlushResult> Flushed(ValueTask<FlushResult> flush)
    {
        if (!flush.IsCompletedSuccessfully) return AwaitFlushed(flush);

        FlushResult result = flush.Result;
        Flushed(result);
        return new ValueTask<FlushResult>(result);
    }

    private async ValueTask<FlushResult> AwaitFlushed(ValueTask<FlushResult> flush)
    {
        FlushResult result = await flush;
        Flushed(result);
        return result;
    }

    private void Flushed(FlushResult result) => _flushStopped |= result.IsCompleted || result.IsCanceled;

    private void Written(ReadOnlySpan<byte> utf8Json)
    {
        if (_observerFailed || utf8Json.IsEmpty) return;

        try
        {
            observer.OnWritten(utf8Json);
        }
        catch (Exception e)
        {
            _observerFailed = true;
            if (logger.IsError) logger.Error($"{observer.GetType().Name} failed to observe a streamed result", e);
        }
    }

    private sealed class ObservingPipeWriter(PipeWriter writer, StreamedResultObservation observation) : PipeWriter
    {
        private Memory<byte> _memory;

        public override Memory<byte> GetMemory(int sizeHint = 0) => _memory = writer.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public override void Advance(int bytes)
        {
            observation.Written(_memory.Span[..bytes]);
            _memory = default;
            writer.Advance(bytes);
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => observation.Flushed(writer.FlushAsync(cancellationToken));

        public override void CancelPendingFlush() => writer.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => writer.Complete(exception);

        public override bool CanGetUnflushedBytes => writer.CanGetUnflushedBytes;

        public override long UnflushedBytes => writer.UnflushedBytes;
    }

    private sealed class ObservingCountingWriter : CountingWriter
    {
        private readonly CountingWriter _writer;
        private readonly StreamedResultObservation _observation;
        private Memory<byte> _memory;

        public ObservingCountingWriter(CountingWriter writer, StreamedResultObservation observation)
        {
            _writer = writer;
            _observation = observation;
            WrittenCount = writer.WrittenCount;
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => _memory = _writer.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public override void Advance(int bytes)
        {
            _observation.Written(_memory.Span[..bytes]);
            _memory = default;
            _writer.Advance(bytes);
            WrittenCount += bytes;
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => _observation.Flushed(_writer.FlushAsync(cancellationToken));

        public override void CancelPendingFlush() => _writer.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => _writer.Complete(exception);

        public override bool CanGetUnflushedBytes => _writer.CanGetUnflushedBytes;

        public override long UnflushedBytes => _writer.UnflushedBytes;
    }
}
