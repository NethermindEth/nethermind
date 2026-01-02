// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Json;

#nullable enable

public abstract class CountingWriter : PipeWriter
{
    public long WrittenCount { get; protected set; }
}

public sealed class CountingPipeWriter : CountingWriter
{
    private readonly PipeWriter _writer;

    public CountingPipeWriter(PipeWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _writer = writer;
    }

    public override void Advance(int count)
    {
        _writer.Advance(count);
        WrittenCount += count;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0) => _writer.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => _writer.GetSpan(sizeHint);

    public override ValueTask CompleteAsync(Exception? exception = null)
        => _writer.CompleteAsync();

    public override void CancelPendingFlush()
        => _writer.CancelPendingFlush();

    public override void Complete(Exception? exception = null)
        => _writer.Complete(exception);

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        => _writer.FlushAsync(cancellationToken);

    public override bool CanGetUnflushedBytes => _writer.CanGetUnflushedBytes;
    public override long UnflushedBytes => _writer.UnflushedBytes;
}

public sealed class CountingStreamPipeWriter : CountingWriter
{
    internal const int InitialSegmentPoolSize = 4; // 16K
    internal const int MaxSegmentPoolSize = 256; // 1MB

    private readonly int _minimumBufferSize;

    private BufferSegment? _head;
    private BufferSegment? _tail;
    private Memory<byte> _tailMemory;
    private int _tailBytesBuffered;
    private int _bytesBuffered;

    private readonly MemoryPool<byte>? _pool;
    private readonly int _maxPooledBufferSize;

    private CancellationTokenSource? _internalTokenSource;
    private bool _isCompleted;
    private readonly Lock _lockObject = new Lock();

    private BufferSegmentStack _bufferSegmentPool;
    private readonly bool _leaveOpen;

    private CancellationTokenSource InternalTokenSource
    {
        get
        {
            lock (_lockObject)
            {
                return _internalTokenSource ??= new CancellationTokenSource();
            }
        }
    }

    public CountingStreamPipeWriter(Stream writingStream, StreamPipeWriterOptions? options = null)
    {
        if (writingStream is null)
        {
            ThrowHelper.ThrowArgumentNullException_WritingStream();
        }
        InnerStream = writingStream;
        _minimumBufferSize = options?.MinimumBufferSize ?? 4096;
        _pool = options?.Pool == MemoryPool<byte>.Shared ? null : options?.Pool;
        _maxPooledBufferSize = _pool?.MaxBufferSize ?? -1;
        _bufferSegmentPool = new BufferSegmentStack(InitialSegmentPoolSize);
        _leaveOpen = options?.LeaveOpen ?? true;
    }

    /// <summary>
    /// Gets the inner stream that is being written to.
    /// </summary>
    public Stream InnerStream { get; }

    /// <inheritdoc />
    public override void Advance(int bytes)
    {
        if ((uint)bytes > (uint)_tailMemory.Length)
        {
            ThrowHelper.ThrowArgumentOutOfRangeException_Bytes();
        }

        _tailBytesBuffered += bytes;
        _bytesBuffered += bytes;
        _tailMemory = _tailMemory[bytes..];
        WrittenCount += bytes;

        if (_bytesBuffered > _minimumBufferSize)
        {
            FlushInternal(writeToStream: true);
        }
    }

    /// <inheritdoc />
    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_isCompleted)
        {
            ThrowHelper.ThrowInvalidOperationException_NoWritingAllowed();
        }

        if (sizeHint < 0)
        {
            ThrowHelper.ThrowArgumentOutOfRangeException_SizeHint();
        }

        AllocateMemory(sizeHint);

        return _tailMemory;
    }

    /// <inheritdoc />
    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        if (_isCompleted)
        {
            ThrowHelper.ThrowInvalidOperationException_NoWritingAllowed();
        }

        if (sizeHint < 0)
        {
            ThrowHelper.ThrowArgumentOutOfRangeException_SizeHint();
        }

        AllocateMemory(sizeHint);

        return _tailMemory.Span;
    }

    private void AllocateMemory(int sizeHint)
    {
        if (_head is null)
        {
            // We need to allocate memory to write since nobody has written before
            BufferSegment newSegment = AllocateSegment(sizeHint);

            // Set all the pointers
            _head = _tail = newSegment;
            _tailBytesBuffered = 0;
        }
        else
        {
            Debug.Assert(_tail is not null);
            int bytesLeftInBuffer = _tailMemory.Length;

            if (bytesLeftInBuffer == 0 || bytesLeftInBuffer < sizeHint)
            {
                if (_tailBytesBuffered > 0)
                {
                    // Flush buffered data to the segment
                    _tail.End += _tailBytesBuffered;
                    _tailBytesBuffered = 0;
                }

                BufferSegment newSegment = AllocateSegment(sizeHint);

                _tail.SetNext(newSegment);
                _tail = newSegment;
            }
        }
    }

    private BufferSegment AllocateSegment(int sizeHint)
    {
        Debug.Assert(sizeHint >= 0);
        BufferSegment newSegment = CreateSegmentUnsynchronized();

        int maxSize = _maxPooledBufferSize;
        if (sizeHint <= maxSize)
        {
            // Use the specified pool as it fits. Specified pool is not null as maxSize == -1 if _pool is null.
            newSegment.SetOwnedMemory(_pool!.Rent(GetSegmentSize(sizeHint, maxSize)));
        }
        else
        {
            // Use the array pool
            int sizeToRequest = GetSegmentSize(sizeHint);
            newSegment.SetOwnedMemory(ArrayPool<byte>.Shared.Rent(sizeToRequest));
        }

        _tailMemory = newSegment.AvailableMemory;

        return newSegment;
    }

    private int GetSegmentSize(int sizeHint, int maxBufferSize = int.MaxValue)
    {
        // First we need to handle case where hint is smaller than minimum segment size
        sizeHint = Math.Max(_minimumBufferSize, sizeHint);
        // After that adjust it to fit into pools max buffer size
        var adjustedToMaximumSize = Math.Min(maxBufferSize, sizeHint);
        return adjustedToMaximumSize;
    }

    private BufferSegment CreateSegmentUnsynchronized()
    {
        if (_bufferSegmentPool.TryPop(out BufferSegment? segment))
        {
            return segment;
        }

        return new BufferSegment();
    }

    private void ReturnSegmentUnsynchronized(BufferSegment segment)
    {
        segment.Reset();
        Interlocked.MemoryBarrier();
        if (_bufferSegmentPool.Count < MaxSegmentPoolSize)
        {
            _bufferSegmentPool.Push(segment);
        }
    }

    /// <inheritdoc />
    public override void CancelPendingFlush()
    {
        Cancel();
    }

    /// <inheritdoc />
    public override bool CanGetUnflushedBytes => true;

    /// <inheritdoc />
    public override void Complete(Exception? exception = null)
    {
        if (_isCompleted)
        {
            return;
        }

        _isCompleted = true;

        try
        {
            FlushInternal(writeToStream: exception is null);
        }
        finally
        {
            _internalTokenSource?.Dispose();

            if (!_leaveOpen)
            {
                InnerStream.Dispose();
            }
        }
    }

    public override async ValueTask CompleteAsync(Exception? exception = null)
    {
        if (_isCompleted)
        {
            return;
        }

        _isCompleted = true;

        try
        {
            await FlushAsyncInternal(writeToStream: exception is null, data: Memory<byte>.Empty).ConfigureAwait(false);
        }
        finally
        {
            _internalTokenSource?.Dispose();

            if (!_leaveOpen)
            {
#if (!NETSTANDARD2_0 && !NETFRAMEWORK)
                await InnerStream.DisposeAsync().ConfigureAwait(false);
#else
                    InnerStream.Dispose();
#endif
            }
        }
    }

    /// <inheritdoc />
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_bytesBuffered == 0)
        {
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
        }

        return FlushAsyncInternal(writeToStream: true, data: Memory<byte>.Empty, cancellationToken);
    }

    /// <inheritdoc />
    public override long UnflushedBytes => _bytesBuffered;

    public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        return FlushAsyncInternal(writeToStream: true, data: source, cancellationToken);
    }

    private void Cancel()
    {
        InternalTokenSource.Cancel();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<FlushResult> FlushAsyncInternal(bool writeToStream, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        // Write all completed segments and whatever remains in the current segment
        // and flush the result.
        CancellationTokenRegistration reg = default;
        if (cancellationToken.CanBeCanceled)
        {
            reg = cancellationToken.UnsafeRegister(static state => ((CountingStreamPipeWriter)state!).Cancel(), this);
        }

        if (_tailBytesBuffered > 0)
        {
            Debug.Assert(_tail is not null);

            // Update any buffered data
            _tail.End += _tailBytesBuffered;
            _tailBytesBuffered = 0;
        }

        using (reg)
        {
            CancellationToken localToken = InternalTokenSource.Token;
            try
            {
                BufferSegment? segment = _head;
                while (segment is not null)
                {
                    BufferSegment returnSegment = segment;
                    segment = segment.NextSegment;

                    if (returnSegment.Length > 0 && writeToStream)
                    {
                        await InnerStream.WriteAsync(returnSegment.Memory, localToken).ConfigureAwait(false);
                    }

                    ReturnSegmentUnsynchronized(returnSegment);

                    // Update the head segment after we return the current segment
                    _head = segment;
                }

                if (writeToStream)
                {
                    // Write data after the buffered data
                    if (data.Length > 0)
                    {
                        await InnerStream.WriteAsync(data, localToken).ConfigureAwait(false);
                    }

                    if (_bytesBuffered > 0 || data.Length > 0)
                    {
                        await InnerStream.FlushAsync(localToken).ConfigureAwait(false);
                    }
                }

                // Mark bytes as written *after* flushing
                _head = null;
                _tail = null;
                _tailMemory = default;
                _bytesBuffered = 0;

                return new FlushResult(isCanceled: false, isCompleted: false);
            }
            catch (OperationCanceledException)
            {
                // Remove the cancellation token such that the next time Flush is called
                // A new CTS is created.
                lock (_lockObject)
                {
                    _internalTokenSource = null;
                }

                if (localToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Catch cancellation and translate it into setting isCanceled = true
                    return new FlushResult(isCanceled: true, isCompleted: false);
                }

                throw;
            }
        }
    }

    private void FlushInternal(bool writeToStream)
    {
        // Write all completed segments and whatever remains in the current segment
        // and flush the result.
        if (_tailBytesBuffered > 0)
        {
            Debug.Assert(_tail is not null);

            // Update any buffered data
            _tail.End += _tailBytesBuffered;
            _tailBytesBuffered = 0;
        }

        BufferSegment? segment = _head;
        while (segment is not null)
        {
            BufferSegment returnSegment = segment;
            segment = segment.NextSegment;

            if (returnSegment.Length > 0 && writeToStream)
            {
                InnerStream.Write(returnSegment.Memory.Span);
            }

            ReturnSegmentUnsynchronized(returnSegment);

            // Update the head segment after we return the current segment
            _head = segment;
        }

        if (_bytesBuffered > 0 && writeToStream)
        {
            InnerStream.Flush();
        }

        // Mark bytes as written *after* flushing
        _head = null;
        _tail = null;
        _tailMemory = default;
        _bytesBuffered = 0;
    }
}
