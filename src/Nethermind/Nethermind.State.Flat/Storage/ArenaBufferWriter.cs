// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Arena-backed <see cref="IByteBufferWriter"/> with a 1 MiB write-buffer plus
/// flush-and-mmap read-back via the <see cref="OpenViewDelegate"/> handed in by the writer.
///
/// Writes are buffered into a pooled byte array and flushed to the underlying
/// <see cref="Stream"/> in 1 MiB chunks. <see cref="OpenReader"/> flushes the
/// pending buffer and the stream, then opens a read-only mmap view over the
/// requested trailing window — the HSST builder uses this to read back the data
/// section it just emitted, so it doesn't need to keep separators/keys in
/// memory while the data section is being written.
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

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (sizeHint > _buffer.Length - _buffered)
            Flush();

        return _buffer.AsSpan(_buffered);
    }

    public void Advance(int count) => _buffered += count;

    public readonly long Written => _flushed + _buffered;

    public readonly long FirstOffset => _firstOffset;

    /// <summary>
    /// Flush pending bytes to the stream and mmap the trailing <paramref name="pastSize"/>
    /// bytes via the supplied <see cref="OpenViewDelegate"/>. The returned reader's
    /// offset 0 corresponds to byte (Written − pastSize) of this writer's data.
    ///
    /// The view is owned by this writer and released on <see cref="Dispose"/>.
    /// Only one reader may be active at a time: calling <see cref="OpenReader"/>
    /// while a prior view is still active throws — the caller must finish using
    /// the previous reader (and let the writer go out of scope, or call
    /// <see cref="DisposeActiveReader"/>) before opening another. Subsequent writes
    /// do not extend the reader's window.
    /// </summary>
    [UnscopedRef]
    public ArenaBufferReader OpenReader(long pastSize)
    {
        if (_activeView is not null)
            throw new InvalidOperationException(
                "ArenaBufferWriter already has an active reader; only one reader is allowed at a time.");
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
        _stream.Dispose();
        byte[] buffer = _buffer;
        _buffer = null!;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}

/// <summary>
/// Pointer-backed reader over an <see cref="IArenaWholeView"/>. The view is owned
/// by the originating <see cref="ArenaBufferWriter"/>; this reader merely borrows
/// its data pointer.
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
