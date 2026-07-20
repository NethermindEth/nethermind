// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Writer that appends trie-node RLPs into a blob arena file. The returned
/// <see cref="NodeRef"/>'s <c>RlpDataOffset</c> is the **file-absolute** offset of the
/// written bytes; many writers across many base snapshots append into the same file
/// over its lifetime, so the id alone is not enough to locate a value.
///
/// <para>
/// Page-aligned padding: before writing an RLP that would otherwise cross a 4 KiB
/// OS-page boundary, leading pad bytes push the value into the next page. The pad
/// is computed against the file-absolute frontier (files start at offset 0). Trie-node
/// RLP is bounded well below 4 KiB (worst-case branch ≈ 532 bytes), so the simple
/// "pad if it would cross" rule never has to split an oversize value. The pad bytes
/// are inert because the reader recovers value bounds from per-entry length
/// metadata.
/// </para>
///
/// <para>
/// The 2 GiB-per-file ceiling stays in force — <c>NodeRef.RlpDataOffset</c> is int32.
/// <see cref="WriteRlp"/> throws <see cref="InvalidOperationException"/> when a write
/// would push the file past <see cref="int.MaxValue"/>. By construction
/// <see cref="BlobArenaManager.CreateWriter"/> only hands out a writer whose target
/// file has headroom for the estimated size, so this throw is a defensive guard
/// against an unusually large RLP late in the writer's life.
/// </para>
/// </summary>
public sealed class BlobArenaWriter : IDisposable
{
    private const int BufferSize = 1024 * 1024;

    private readonly IBlobArenaManager _manager;
    private readonly BlobArenaFile _file;
    private readonly ushort _blobArenaId;
    private readonly long _startOffset;
    private readonly Stream _stream;
    // Held at Count == Capacity so AsSpan() exposes the whole 1 MiB buffer; the writer slices
    // the free tail with its own _buffered cursor (same shape as ArenaBufferWriter).
    private readonly NativeMemoryList<byte> _buffer = new(BufferSize, BufferSize);
    private int _buffered;
    // File-absolute offset of the next byte to write. Starts at _startOffset (the file's
    // frontier when this writer was opened) and advances with each write and any inserted
    // pad bytes. The 2 GiB cap is per file: a writer that starts at frontier F can only
    // write up to int.MaxValue - F more bytes.
    private long _written;
    private bool _completed;
    private bool _disposed;

    /// <summary>
    /// The writer holds a lease on <paramref name="file"/> acquired by
    /// <see cref="BlobArenaManager.CreateWriter"/> via <see cref="BlobArenaFile.TryAcquireLease"/>.
    /// Disposal drops the lease via <see cref="RefCountingDisposable.Dispose"/>; if no
    /// snapshot picked the file up via <see cref="BlobArenaManager.TryLeaseFile"/> in the
    /// meantime, the file self-cleans (manager's array-slot ref is still 1, so the file
    /// stays alive — it only goes away on manager shutdown or sweep).
    /// </summary>
    internal BlobArenaWriter(IBlobArenaManager manager, BlobArenaFile file, long startOffset, Stream stream)
    {
        _manager = manager;
        _file = file;
        _blobArenaId = file.BlobArenaId;
        _startOffset = startOffset;
        _written = startOffset;
        _stream = stream;
    }

    /// <summary>
    /// The blob arena file id embedded in every <see cref="NodeRef"/> returned by <see cref="WriteRlp"/>.
    /// </summary>
    public ushort BlobArenaId => _blobArenaId;

    /// <summary>
    /// File-absolute offset of the next byte this writer will append (post-padding).
    /// </summary>
    public long Written => _written;

    /// <summary>
    /// File-absolute offset of the first byte this writer appends — the start of the
    /// contiguous RLP region it produces. Equals the file's frontier when the writer opened.
    /// </summary>
    public long StartOffset => _startOffset;

    /// <summary>
    /// Append <paramref name="rlp"/> to the blob arena file, padding to keep it within a
    /// single 4 KiB page when it would otherwise straddle. Returns the <see cref="NodeRef"/>
    /// that the caller embeds in the metadata table in place of the inline RLP.
    /// </summary>
    public NodeRef WriteRlp(ReadOnlySpan<byte> rlp)
    {
        if (_completed || _disposed)
            throw new InvalidOperationException("BlobArenaWriter is closed.");

        long offsetInPage = _written & PageLayout.PageMask;
        if (rlp.Length <= PageLayout.PageSize && offsetInPage != 0 && offsetInPage + rlp.Length > PageLayout.PageSize)
        {
            int pad = (int)(PageLayout.PageSize - offsetInPage);
            EnsureBufferSpace(pad)[..pad].Clear();
            _buffered += pad;
            _written += pad;
        }

        if (_written + rlp.Length > int.MaxValue)
            throw new InvalidOperationException(
                $"BlobArenaWriter for blob arena {_blobArenaId} would exceed the 2 GiB per-file NodeRef offset ceiling.");

        int offset = (int)_written;
        // Trie-node RLP is bounded well below the buffer size (worst-case branch ≈ 532 B), so
        // EnsureBufferSpace always returns room for the whole value in one copy.
        rlp.CopyTo(EnsureBufferSpace(rlp.Length));
        _buffered += rlp.Length;
        _written += rlp.Length;
        return new NodeRef(_blobArenaId, offset);
    }

    /// <summary>
    /// Finalise the write: flush the in-memory buffer to the file and register the new
    /// frontier with the manager. The writer's own lease on the file is still held — it
    /// is released by <see cref="Dispose"/>. <see cref="PersistedSnapshots.PersistedSnapshotRepository"/>
    /// takes its own snapshot lease via <see cref="BlobArenaManager.TryLeaseFile"/> before
    /// this writer is disposed.
    /// </summary>
    public void Complete()
    {
        if (_completed) throw new InvalidOperationException("BlobArenaWriter already completed.");
        FlushBuffer();
        _stream.Flush();
        _stream.Dispose();
        _completed = true;
        // Writer mutates the file directly. Manager learns whether the id is still a
        // candidate for the next writer's packing scan and pushes the post-write
        // frontier delta to the per-tier allocated-bytes gauge.
        _file.Frontier = _written;
        _manager.OnWriteCompleted(_file, hasHeadroom: _file.Frontier < _file.MaxSize);
    }

    /// <summary>
    /// <c>fsync(2)</c> the underlying blob file. Must be called after <see cref="Complete"/>
    /// — Complete flushes the writer's in-memory buffer through the FileStream; this method
    /// blocks until those bytes are durable on disk. Used by the persisted-snapshot convert
    /// path on base snapshots before the catalog records the new entry.
    /// </summary>
    public void Fsync()
    {
        if (!_completed) throw new InvalidOperationException("BlobArenaWriter.Fsync requires Complete first.");
        _file.Fsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_completed)
        {
            _stream.Dispose();
            // Cancelled mid-write — frontier didn't advance, so the file still has room.
            // Manager re-adds the id to the mutable pool without touching the file.
            _manager.OnWriteCancelled(_blobArenaId);
        }
        _buffer.Dispose();
        // Drop the writer's lease on the file. If a snapshot has already picked the file
        // up via TryLeaseFile, this just decrements one lease; if nobody else holds a
        // lease, the file stays alive on the manager's array-slot ref until shutdown / sweep.
        _file.Dispose();
    }

    private Span<byte> EnsureBufferSpace(int sizeHint)
    {
        if (sizeHint > _buffer.Count - _buffered) FlushBuffer();
        return _buffer.AsSpan()[_buffered..];
    }

    private void FlushBuffer()
    {
        if (_buffered == 0) return;
        _stream.Write(_buffer.AsSpan()[.._buffered]);
        _buffered = 0;
    }
}
