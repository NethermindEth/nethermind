// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Hsst;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Writer over a freshly-allocated blob arena reservation. Trie-node RLPs are appended
/// back-to-back; each call to <see cref="WriteRlp"/> returns the <see cref="NodeRef"/>
/// that locates the just-written item.
///
/// <para>
/// Page-aligned padding mirrors <c>PersistedSnapshotBuilder.WriteTrieNodeRlpPageAligned</c>:
/// before writing an RLP that would otherwise cross a 4 KiB OS-page boundary, leading
/// pad bytes push the value into the next page. Trie-node RLP is bounded well below
/// 4 KiB (worst-case branch ≈ 532 bytes), so the simple "pad if it would cross" rule
/// never has to split an oversize value. The pad bytes are inert because the HSST
/// reader recovers value bounds from per-entry length metadata.
/// </para>
///
/// <para>
/// The 2 GiB-per-reservation ceiling stays in force — <c>NodeRef.RlpDataOffset</c> is
/// int32. Pass 1 throws <see cref="InvalidOperationException"/> when a write would
/// push the reservation past <see cref="int.MaxValue"/>; pass 2 introduces rollover
/// to a fresh blob arena id mid-write so a single snapshot can spill across multiple
/// blob arenas.
/// </para>
/// </summary>
public sealed class BlobArenaWriter : IDisposable
{
    private const int PageSize = 4096;

    private readonly BlobArenaManager _manager;
    private readonly ArenaWriter _inner;
    private readonly int _blobArenaId;
    private long _written;
    private bool _completed;
    private bool _disposed;

    internal BlobArenaWriter(BlobArenaManager manager, int blobArenaId, ArenaWriter inner)
    {
        _manager = manager;
        _blobArenaId = blobArenaId;
        _inner = inner;
    }

    /// <summary>
    /// The global blob arena id that <see cref="WriteRlp"/> embeds in returned
    /// <see cref="NodeRef"/>s. Stable for the writer's lifetime.
    /// </summary>
    public int BlobArenaId => _blobArenaId;

    /// <summary>
    /// Bytes written into this blob arena reservation so far, including pad bytes.
    /// </summary>
    public long Written => _written;

    /// <summary>
    /// Append <paramref name="rlp"/> to the blob arena, padding to keep it within a single
    /// 4 KiB page when it would otherwise straddle. Returns the <see cref="NodeRef"/>
    /// that the caller embeds in the metadata HSST in place of the inline RLP.
    /// </summary>
    public NodeRef WriteRlp(ReadOnlySpan<byte> rlp)
    {
        if (_completed || _disposed)
            throw new InvalidOperationException("BlobArenaWriter is closed.");

        ref ArenaBufferWriter bw = ref _inner.GetWriter();
        long offsetInPage = (bw.Written - bw.FirstOffset) & (PageSize - 1);
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
                $"BlobArenaWriter for blob arena {_blobArenaId} would exceed the 2 GiB NodeRef offset ceiling. " +
                "Pass-2 rollover not yet implemented.");

        int offset = (int)_written;
        IByteBufferWriter.Copy(ref bw, rlp);
        _written += rlp.Length;
        return new NodeRef(_blobArenaId, offset);
    }

    /// <summary>
    /// Finalise the underlying arena reservation and register it with the manager
    /// under <see cref="BlobArenaId"/>. After this call the blob arena is readable
    /// via <see cref="BlobArenaManager.RandomRead"/>.
    /// </summary>
    public ArenaReservation Complete()
    {
        if (_completed) throw new InvalidOperationException("BlobArenaWriter already completed.");
        (SnapshotLocation _, ArenaReservation reservation) = _inner.Complete();
        _completed = true;
        _manager.RegisterCompleted(_blobArenaId, reservation);
        return reservation;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // If Complete() was never called, ArenaWriter.Dispose cancels the underlying
        // write and deletes the dedicated file (if any). The pre-allocated blob arena
        // id is simply abandoned — the id counter advances monotonically and nothing
        // ever references it.
        _inner.Dispose();
    }
}
