// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;

namespace Nethermind.State.Flat.Storage;

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
/// are inert because the HSST reader recovers value bounds from per-entry length
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
    private const int PageSize = 4096;
    private const int BufferSize = 1024 * 1024;

    private readonly BlobArenaManager _manager;
    private readonly BlobArenaFile _file;
    private readonly ushort _blobArenaId;
    private readonly long _startOffset;
    private readonly FileStream _stream;
    private byte[] _buffer;
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
    internal BlobArenaWriter(BlobArenaManager manager, BlobArenaFile file, long startOffset, FileStream stream)
    {
        _manager = manager;
        _file = file;
        _blobArenaId = file.BlobArenaId;
        _startOffset = startOffset;
        _written = startOffset;
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <summary>
    /// The blob arena file id that <see cref="WriteRlp"/> embeds in returned
    /// <see cref="NodeRef"/>s. Equals the underlying <see cref="ArenaFile.Id"/>.
    /// </summary>
    public ushort BlobArenaId => _blobArenaId;

    /// <summary>
    /// File-absolute offset of the next byte this writer will append (post-padding).
    /// </summary>
    public long Written => _written;

    /// <summary>
    /// Append <paramref name="rlp"/> to the blob arena file, padding to keep it within a
    /// single 4 KiB page when it would otherwise straddle. Returns the <see cref="NodeRef"/>
    /// that the caller embeds in the metadata HSST in place of the inline RLP.
    /// </summary>
    public NodeRef WriteRlp(ReadOnlySpan<byte> rlp)
    {
        if (_completed || _disposed)
            throw new InvalidOperationException("BlobArenaWriter is closed.");

        long offsetInPage = _written & (PageSize - 1);
        if (rlp.Length <= PageSize && offsetInPage != 0 && offsetInPage + rlp.Length > PageSize)
        {
            int pad = (int)(PageSize - offsetInPage);
            EnsureBufferSpace(pad)[..pad].Clear();
            _buffered += pad;
            _written += pad;
        }

        if (_written + rlp.Length > int.MaxValue)
            throw new InvalidOperationException(
                $"BlobArenaWriter for blob arena {_blobArenaId} would exceed the 2 GiB per-file NodeRef offset ceiling.");

        int offset = (int)_written;
        ReadOnlySpan<byte> remaining = rlp;
        while (remaining.Length > 0)
        {
            Span<byte> dst = EnsureBufferSpace(remaining.Length);
            int chunk = Math.Min(remaining.Length, dst.Length);
            remaining[..chunk].CopyTo(dst);
            _buffered += chunk;
            remaining = remaining[chunk..];
        }
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
        _manager.RegisterCompleted(_blobArenaId, _startOffset, _written - _startOffset);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_completed)
        {
            _stream.Dispose();
            _manager.CancelWrite(_blobArenaId);
        }
        byte[] buffer = _buffer;
        _buffer = null!;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        // Drop the writer's lease on the file. If a snapshot has already picked the file
        // up via TryLeaseFile, this just decrements one lease; if nobody else holds a
        // lease, the file stays alive on the manager's array-slot ref until shutdown / sweep.
        _file.Dispose();
    }

    private Span<byte> EnsureBufferSpace(int sizeHint)
    {
        if (sizeHint > _buffer.Length - _buffered) FlushBuffer();
        return _buffer.AsSpan(_buffered);
    }

    private void FlushBuffer()
    {
        if (_buffered == 0) return;
        _stream.Write(_buffer, 0, _buffered);
        _buffered = 0;
    }
}
