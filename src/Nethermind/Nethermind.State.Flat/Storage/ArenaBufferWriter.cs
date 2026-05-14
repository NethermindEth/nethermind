// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Arena-backed <see cref="IByteBufferWriter"/> with a 1 MiB write-buffer plus
/// read-back via the <see cref="OpenViewDelegate"/> handed in by the writer.
///
/// Writes are buffered into a pooled byte array and flushed to the underlying
/// <see cref="Stream"/> in 1 MiB chunks. <see cref="OpenReader"/> exposes a read
/// view over the trailing <c>pastSize</c> bytes of writer-relative data. When
/// that window still sits entirely in the unflushed buffer, the reader is
/// constructed directly over the pinned buffer — no flush, no mmap. Otherwise
/// the buffer is flushed and the trailing window is mmap'd from the underlying
/// file (the original behaviour).
///
/// While a buffer-backed reader is active the buffer is pinned via a
/// <see cref="GCHandle"/>. Subsequent writes append at <c>_buffered</c>; if a
/// write would overflow the buffer the writer "promotes" by writing the current
/// bytes through to the stream and renting a fresh buffer as the new write
/// target. The original pinned buffer stays alive (the reader keeps reading
/// from it) until <see cref="DisposeActiveReader"/>, at which point it is
/// unpinned and returned to the pool. On reader release, if the current buffer
/// is more than 3/4 full it is flushed so the next builder has headroom to take
/// the fast path too.
/// </summary>
public unsafe struct ArenaBufferWriter(Stream stream, long firstOffset, ArenaBufferWriter.OpenViewDelegate openView)
    : IByteBufferWriterWithReader<ArenaBufferReader, NoOpPin>, IDisposable
{
    private const int BufferSize = 1024 * 1024; // 1 MiB

    /// <summary>
    /// Opens a read view over the writer-relative range
    /// <c>[relativeOffset, relativeOffset + size)</c> of the just-written data.
    /// Implementations are expected to dispose the returned view when the caller
    /// disposes it (e.g. mmap accessor + MADV_DONTNEED on Linux).
    /// </summary>
    public delegate IArenaWholeView OpenViewDelegate(long relativeOffset, long size);

    private readonly Stream _stream = stream;
    private readonly OpenViewDelegate _openView = openView;
    private readonly long _firstOffset = firstOffset;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _buffered;
    private long _flushed;
    private IArenaWholeView? _activeView;

    // When a buffer-backed reader is active, _pinnedReaderBuffer holds the
    // byte[] the reader is reading from and _pinnedReaderHandle pins it.
    // Initially equals _buffer; promote-on-overflow rents a new _buffer and the
    // two diverge — the reader keeps reading from the pinned shadowed buffer
    // while subsequent writes continue into the new one.
    private byte[]? _pinnedReaderBuffer;
    private GCHandle _pinnedReaderHandle;

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (sizeHint > _buffer.Length - _buffered)
        {
            if (_pinnedReaderBuffer is not null)
                PromoteBufferForActiveReader(sizeHint);
            else
                Flush();
        }

        return _buffer.AsSpan(_buffered);
    }

    public void Advance(int count) => _buffered += count;

    public readonly long Written => _flushed + _buffered;

    public readonly long FirstOffset => _firstOffset;

    /// <summary>
    /// Open a reader over the trailing <paramref name="pastSize"/> bytes of
    /// writer-relative data. When the entire window still sits in the unflushed
    /// buffer this pins the buffer and hands back a pointer into it directly
    /// (no flush, no mmap). Otherwise the buffer is flushed and the trailing
    /// window is mmap'd via the supplied <see cref="OpenViewDelegate"/>. The
    /// returned reader's offset 0 corresponds to byte (Written − pastSize) of
    /// this writer's data.
    ///
    /// The view (mmap or pinned buffer) is owned by this writer and released on
    /// <see cref="DisposeActiveReader"/> or <see cref="Dispose"/>. Only one
    /// reader may be active at a time: calling <see cref="OpenReader"/> while a
    /// prior view is still active throws. Subsequent writes do not extend the
    /// reader's window.
    /// </summary>
    [UnscopedRef]
    public ArenaBufferReader OpenReader(long pastSize)
    {
        if (_activeView is not null || _pinnedReaderBuffer is not null)
            throw new InvalidOperationException(
                "ArenaBufferWriter already has an active reader; only one reader is allowed at a time.");

        // Fast path: requested window is still entirely in the unflushed buffer.
        // Pin the buffer and hand back a pointer into it — no syscalls.
        if (_buffered >= pastSize)
        {
            int bufferOffset = _buffered - checked((int)pastSize);
            _pinnedReaderHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            _pinnedReaderBuffer = _buffer;
            byte* ptr = (byte*)_pinnedReaderHandle.AddrOfPinnedObject() + bufferOffset;
            return new ArenaBufferReader(ptr, pastSize);
        }

        // Slow path: window straddles already-flushed bytes — flush remainder
        // and mmap the trailing region from the underlying file.
        Flush();
        long writerWindowStart = Written - pastSize;
        _activeView = _openView(writerWindowStart, pastSize);
        return new ArenaBufferReader(_activeView.DataPtr, pastSize);
    }

    /// <summary>
    /// Release the view opened by the most recent <see cref="OpenReader"/> call.
    /// Any outstanding <see cref="ArenaBufferReader"/> borrowed from this writer
    /// must no longer be used after this returns.
    /// </summary>
    public void DisposeActiveReader()
    {
        if (_pinnedReaderBuffer is not null)
        {
            byte[] pinned = _pinnedReaderBuffer;
            _pinnedReaderBuffer = null;
            _pinnedReaderHandle.Free();
            _pinnedReaderHandle = default;
            // If a promote-on-overflow shadowed the pinned buffer it is no
            // longer the current _buffer — return it to the pool.
            if (!ReferenceEquals(pinned, _buffer))
                ArrayPool<byte>.Shared.Return(pinned);

            // Flush proactively when the current buffer is past 3/4 full so the
            // next OpenReader has headroom to take the fast path.
            if (_buffered >= (_buffer.Length / 4) * 3)
                Flush();
            return;
        }

        _activeView?.Dispose();
        _activeView = null;
    }

    public void Flush()
    {
        if (_buffered > 0)
        {
            _stream.Write(_buffer, 0, _buffered);
            _flushed += _buffered;
            _buffered = 0;
        }
        _stream.Flush();
    }

    public void Dispose()
    {
        Flush();
        _activeView?.Dispose();
        _activeView = null;
        if (_pinnedReaderBuffer is not null)
        {
            byte[] pinned = _pinnedReaderBuffer;
            _pinnedReaderBuffer = null;
            _pinnedReaderHandle.Free();
            _pinnedReaderHandle = default;
            if (!ReferenceEquals(pinned, _buffer))
                ArrayPool<byte>.Shared.Return(pinned);
        }
        _stream.Dispose();
        byte[] buffer = _buffer;
        _buffer = null!;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>
    /// Called when a write would overflow the buffer but a buffer-backed reader
    /// holds the current buffer pinned. Writes the current buffered bytes
    /// through to the stream (a copy — the reader's bytes stay intact in
    /// memory) and swaps in a freshly-rented buffer as the new write target.
    /// The pinned buffer is retained until the reader is released.
    /// </summary>
    private void PromoteBufferForActiveReader(int sizeHint)
    {
        if (_buffered > 0)
        {
            _stream.Write(_buffer, 0, _buffered);
            _flushed += _buffered;
            _buffered = 0;
        }

        int requested = sizeHint > BufferSize ? sizeHint : BufferSize;
        // Do NOT return _buffer to the pool — it's still pinned for the reader.
        _buffer = ArrayPool<byte>.Shared.Rent(requested);
    }
}

/// <summary>
/// Pointer-backed reader over an <see cref="IArenaWholeView"/> or pinned write
/// buffer. The backing memory is owned by the originating
/// <see cref="ArenaBufferWriter"/>; this reader merely borrows its data pointer.
/// </summary>
public readonly unsafe ref struct ArenaBufferReader : IHsstByteReader<NoOpPin>
{
    private readonly byte* _ptr;
    private readonly long _length;

    internal ArenaBufferReader(byte* ptr, long length)
    {
        _ptr = ptr;
        _length = length;
    }

    public long Length => _length;

    public Bound Bound => new(0, _length);

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset > (ulong)(_length - output.Length)) return false;
        new ReadOnlySpan<byte>(_ptr + offset, output.Length).CopyTo(output);
        return true;
    }

    public NoOpPin PinBuffer(long offset, long size)
    {
        if ((ulong)offset + (ulong)size > (ulong)_length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return new NoOpPin(new ReadOnlySpan<byte>(_ptr + offset, checked((int)size)));
    }
}
