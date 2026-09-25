// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IO;

namespace Nethermind.Serialization.Json;

/// <summary>Writes directly into a recyclable memory stream and supports discarding a failed response.</summary>
/// <remarks>
/// The caller owns the stream and must not reposition or write to it while using this writer.
/// Rewinding truncates appended bytes; it cannot restore bytes overwritten before the stream's end.
/// </remarks>
public sealed class RewindableStreamPipeWriter : CountingWriter
{
    private readonly RecyclableMemoryStream _stream;
    private readonly long _initialWrittenCount;
    private bool _completed;
    private int _flushCanceled;

    /// <summary>Creates a writer over a memory stream positioned at its end.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException">The stream's position is not at its end.</exception>
    public RewindableStreamPipeWriter(RecyclableMemoryStream stream, long initialWrittenCount = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Position != stream.Length) throw new ArgumentException("The stream must be positioned at its end.", nameof(stream));
        _stream = stream;
        _initialWrittenCount = WrittenCount = initialWrittenCount;
    }

    /// <summary>Discards bytes written after the supplied count, preserving earlier output.</summary>
    /// <param name="writtenCount">A checkpoint previously read from <see cref="CountingWriter.WrittenCount"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="writtenCount"/> is outside the range from the initial count through <see cref="CountingWriter.WrittenCount"/>.
    /// </exception>
    public void Rewind(long writtenCount)
    {
        if (writtenCount < _initialWrittenCount || writtenCount > WrittenCount) throw new ArgumentOutOfRangeException(nameof(writtenCount));
        long length = _stream.Length - (WrittenCount - writtenCount);
        _stream.SetLength(length);
        _stream.Position = length;
        WrittenCount = writtenCount;
    }

    /// <inheritdoc/>
    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_completed) throw new InvalidOperationException("The writer is completed.");
        return _stream.GetMemory(sizeHint);
    }

    /// <inheritdoc/>
    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    /// <inheritdoc/>
    public override void Advance(int bytes)
    {
        _stream.Advance(bytes);
        WrittenCount += bytes;
    }

    /// <inheritdoc/>
    public override bool CanGetUnflushedBytes => true;
    /// <inheritdoc/>
    public override long UnflushedBytes => 0;

    /// <inheritdoc/>
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(new FlushResult(Interlocked.Exchange(ref _flushCanceled, 0) != 0, false));
    }

    /// <inheritdoc/>
    public override void CancelPendingFlush() => Interlocked.Exchange(ref _flushCanceled, 1);

    /// <inheritdoc/>
    public override void Complete(Exception? exception = null) => _completed = true;
}
