// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Test.IO;

/// <summary>
/// A pipe writer that records how many bytes accumulate between flushes, the most a transport has to hold
/// before a flush can apply backpressure.
/// </summary>
/// <remarks>Written bytes are kept only when <c>keepOutput</c> is set; otherwise one scratch buffer is reused.</remarks>
public sealed class FlushRecordingPipeWriter(bool keepOutput) : PipeWriter
{
    private const int MinimumBufferSize = 4096;

    private readonly ArrayBufferWriter<byte>? _output = keepOutput ? new ArrayBufferWriter<byte>() : null;
    private byte[] _scratch = new byte[MinimumBufferSize];
    private long _unflushedBytes;
    private long _maxBytesAtFlush;

    public long TotalBytes { get; private set; }
    public int FlushCount { get; private set; }
    public long MaxUnflushedBytes => Math.Max(_maxBytesAtFlush, _unflushedBytes);
    public ReadOnlySpan<byte> WrittenSpan => _output is null ? throw new InvalidOperationException("Output is not kept.") : _output.WrittenSpan;

    public override bool CanGetUnflushedBytes => true;
    public override long UnflushedBytes => _unflushedBytes;

    public override void Advance(int bytes)
    {
        _output?.Advance(bytes);
        _unflushedBytes += bytes;
        TotalBytes += bytes;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_output is not null) return _output.GetMemory(Math.Max(sizeHint, MinimumBufferSize));
        if (_scratch.Length < sizeHint) _scratch = new byte[sizeHint];
        return _scratch;
    }

    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        _maxBytesAtFlush = Math.Max(_maxBytesAtFlush, _unflushedBytes);
        _unflushedBytes = 0;
        FlushCount++;
        return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
    }

    public override void CancelPendingFlush() { }

    public override void Complete(Exception? exception = null) { }
}
