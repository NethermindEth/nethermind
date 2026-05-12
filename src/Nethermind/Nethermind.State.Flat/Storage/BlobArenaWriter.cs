// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;

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

    private readonly BlobArenaManager _manager;
    private readonly ArenaWriter _inner;
    private readonly ushort _blobArenaId;
    private readonly long _startOffset;
    // File-absolute offset of the next byte to write. Starts at _startOffset (the file's
    // frontier when this writer was opened) and advances with each write and any inserted
    // pad bytes. The 2 GiB cap is per file: a writer that starts at frontier F can only
    // write up to int.MaxValue - F more bytes.
    private long _written;
    private bool _completed;
    private bool _disposed;

    internal BlobArenaWriter(BlobArenaManager manager, ushort blobArenaId, long startOffset, ArenaWriter inner)
    {
        _manager = manager;
        _blobArenaId = blobArenaId;
        _startOffset = startOffset;
        _written = startOffset;
        _inner = inner;
    }

    /// <summary>
    /// The blob arena file id that <see cref="WriteRlp"/> embeds in returned
    /// <see cref="NodeRef"/>s. Equals the underlying <c>ArenaFile.Id</c>.
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

        ref ArenaBufferWriter bw = ref _inner.GetWriter();
        long offsetInPage = _written & (PageSize - 1);
        if (rlp.Length <= PageSize && offsetInPage != 0 && offsetInPage + rlp.Length > PageSize)
        {
            int pad = (int)(PageSize - offsetInPage);
            Span<byte> padSpan = bw.GetSpan(pad);
            padSpan[..pad].Clear();
            bw.Advance(pad);
            _written += pad;
        }

        if (_written + rlp.Length > int.MaxValue)
            throw new InvalidOperationException(
                $"BlobArenaWriter for blob arena {_blobArenaId} would exceed the 2 GiB per-file NodeRef offset ceiling.");

        int offset = (int)_written;
        IByteBufferWriter.Copy(ref bw, rlp);
        _written += rlp.Length;
        return new NodeRef(_blobArenaId, offset);
    }

    /// <summary>
    /// Finalise the underlying arena write and register the new frontier with the manager.
    /// On first registration of a given file id the manager opens a single whole-file
    /// <see cref="ArenaReservation"/>; subsequent writers for the same file grow that
    /// reservation's <c>Size</c>. The writer's transient creation lease is dropped via
    /// <see cref="BlobArenaManager.ReleaseBlobArena"/> after the owning snapshot has
    /// acquired its own lease.
    /// </summary>
    public void Complete()
    {
        if (_completed) throw new InvalidOperationException("BlobArenaWriter already completed.");
        _inner.CompleteSliceless();
        _completed = true;
        _manager.RegisterCompleted(_blobArenaId, _startOffset, _written - _startOffset);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // If Complete() was never called, ArenaWriter.Dispose cancels the underlying
        // write (deletes dedicated files; clears the reservation flag on shared files).
        // No catalog/refcount touch needed — RegisterCompleted is what introduces a
        // file-level lease in the first place.
        _inner.Dispose();
    }
}
