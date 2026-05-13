// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A blob arena file storing trie-node RLP bytes. Owns its <see cref="SafeFileHandle"/>
/// and is refcounted: the owning <see cref="BlobArenaManager"/>'s array slot holds the
/// initial lease (count 1), the issuing <see cref="BlobArenaWriter"/> and every leased
/// <see cref="PersistedSnapshots.PersistedSnapshot"/> hold additional ones. The on-disk
/// file is deleted by <see cref="CleanUp"/> when the last lease is released, unless
/// <see cref="PersistOnShutdown"/> was called first — in which case the file is preserved
/// for the next session.
///
/// <para>
/// Reads use <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/> directly:
/// no mmap, no page tracker, no advise — the blob path is pure <c>pread</c>.
/// </para>
///
/// <para>
/// Owns its own contribution to <see cref="Metrics.ArenaReservationCountByTag"/> /
/// <see cref="Metrics.ArenaReservationBytesByTag"/> under <see cref="_reservationTag"/>:
/// the count gauge is bumped on construction and dropped on <see cref="CleanUp"/>; the
/// bytes gauge grows via <see cref="OnFrontierGrew"/> as the file is appended to.
/// </para>
/// </summary>
public sealed class BlobArenaFile : RefCountingDisposable
{
    // Treated as bool; 0 = delete on CleanUp, 1 = keep the on-disk file. Set by
    // PersistOnShutdown via Interlocked.Exchange so it is safe to call from any path.
    private int _preserveOnDispose;

    private readonly string _reservationTag;
    // Cumulative bytes this file has added to ArenaReservationBytesByTag — used by
    // CleanUp to balance the gauge symmetrically with the increments we emitted.
    private long _registeredBytes;

    /// <summary>Stable file id, narrowed from int to ushort. Embedded in every <see cref="NodeRef"/>.</summary>
    public ushort BlobArenaId { get; }

    /// <summary>On-disk path. Deleted by <see cref="CleanUp"/> unless <see cref="PersistOnShutdown"/> opted in.</summary>
    public string Path { get; }

    /// <summary>Pre-extended file length (sparse on Linux). Writers append within this cap.</summary>
    public long MaxSize { get; }

    /// <summary>Underlying read/write file handle. Borrowed by leases for direct <c>pread</c>.</summary>
    internal SafeFileHandle Handle { get; }

    /// <summary>Next-write offset. Mutated under the manager's lock during writer registration.</summary>
    internal long Frontier { get; set; }

    internal BlobArenaFile(string reservationTag, ushort id, string path, long maxSize, long frontier)
    {
        _reservationTag = reservationTag;
        BlobArenaId = id;
        Path = path;
        MaxSize = maxSize;
        Handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        // Pre-extend file to MaxSize if smaller (sparse on Linux via ftruncate). Subsequent
        // appends never have to grow the file.
        if (RandomAccess.GetLength(Handle) < maxSize)
            RandomAccess.SetLength(Handle, maxSize);
        Frontier = frontier;
        // Register one count immediately; the bytes gauge gets seeded with whatever the
        // on-disk file already contains (Initialize-loaded files). Fresh writer-created
        // files start at 0 and grow via OnFrontierGrew on RegisterCompleted.
        Metrics.ArenaReservationCountByTag.AddOrUpdate(reservationTag, 1L, static (_, c) => c + 1);
        if (frontier > 0)
        {
            _registeredBytes = frontier;
            Metrics.ArenaReservationBytesByTag.AddOrUpdate(reservationTag,
                static (_, s) => s, static (_, b, s) => b + s, frontier);
        }
    }

    /// <summary>
    /// Mark this file as "preserve on disk when its refcount hits zero". Set by
    /// <see cref="PersistedSnapshots.PersistedSnapshot.PersistOnShutdown"/> for every blob
    /// arena that a still-loaded snapshot references, so the file survives manager
    /// teardown and is rehydrated by the next session's <see cref="BlobArenaManager.Initialize"/>.
    /// Idempotent.
    /// </summary>
    public void PersistOnShutdown() => Interlocked.Exchange(ref _preserveOnDispose, 1);

    /// <summary>
    /// Defensive lease acquisition; returns false when the file has already entered
    /// <see cref="CleanUp"/>. Promotes <see cref="RefCountingDisposable.TryAcquireLease"/>
    /// from protected to internal so the owning manager can lease under its lock.
    /// </summary>
    internal new bool TryAcquireLease() => base.TryAcquireLease();

    /// <summary>
    /// True iff the file's refcount is exactly 1 — i.e. the only outstanding lease is
    /// the manager's array slot. Used by <see cref="BlobArenaManager.SweepUnreferenced"/>
    /// to detect post-restart orphans (Initialize-loaded files that no snapshot has
    /// leased) so the manager can drop its slot and let <see cref="CleanUp"/> delete
    /// the on-disk file.
    /// </summary>
    internal bool HasOnlyManagerLease => Volatile.Read(ref _leases.Value) == 1;

    /// <summary>
    /// Read <paramref name="destination"/>.Length bytes starting at
    /// <paramref name="offset"/> from this blob arena file via
    /// <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/>. Loops over
    /// short reads until either the destination is full or a 0-byte read signals
    /// end-of-data. Returns the total bytes copied into <paramref name="destination"/>
    /// (may be less than the destination length on short read at end-of-file).
    /// </summary>
    public int RandomRead(long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(Handle, destination[total..], offset + total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    /// <summary>
    /// Open a write stream seeked to <paramref name="startOffset"/>. Caller disposes when done.
    /// </summary>
    internal FileStream OpenWriteStream(long startOffset)
    {
        FileStream fs = new(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1);
        fs.Seek(startOffset, SeekOrigin.Begin);
        return fs;
    }

    /// <summary>
    /// Add <paramref name="delta"/> bytes to this file's contribution to the bytes gauge.
    /// Called by <see cref="BlobArenaManager.RegisterCompleted"/> after a writer commits a
    /// new frontier so the gauge tracks file growth in real time.
    /// </summary>
    internal void OnFrontierGrew(long delta)
    {
        if (delta <= 0) return;
        _registeredBytes += delta;
        Metrics.ArenaReservationBytesByTag.AddOrUpdate(_reservationTag,
            static (_, s) => s, static (_, b, s) => b + s, delta);
    }

    protected override void CleanUp()
    {
        Handle.Dispose();
        // Preserve the on-disk file iff someone explicitly opted in via PersistOnShutdown;
        // otherwise delete it (the normal post-prune cleanup path).
        if (Volatile.Read(ref _preserveOnDispose) == 0)
        {
            try { File.Delete(Path); } catch { /* best-effort */ }
        }
        // Symmetric drop: one count, _registeredBytes bytes.
        Metrics.ArenaReservationCountByTag.AddOrUpdate(_reservationTag,
            0L, static (_, c) => Math.Max(0, c - 1));
        if (_registeredBytes > 0)
        {
            Metrics.ArenaReservationBytesByTag.AddOrUpdate(_reservationTag,
                static (_, _) => 0L, static (_, b, s) => Math.Max(0, b - s), _registeredBytes);
        }
    }
}
