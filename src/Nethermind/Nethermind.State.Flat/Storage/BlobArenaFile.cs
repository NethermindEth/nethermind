// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Win32.SafeHandles;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A handle held by a <see cref="PersistedSnapshots.PersistedSnapshot"/> onto one
/// referenced blob arena file. Owns no file resource of its own — borrows a
/// <see cref="SafeFileHandle"/> from the issuing <see cref="IBlobArenaManager"/>,
/// which keeps the file open as long as at least one lease is alive. Reads use the
/// borrowed handle directly via <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/>;
/// no mmap, no page tracker, no advise — the blob path is pure <c>pread</c>.
///
/// <para>
/// Lifecycle: created by <see cref="IBlobArenaManager.TryLeaseFile"/> with a fresh
/// lease on the underlying file's refcount. The caller (typically
/// <c>PersistedSnapshotRepository</c>) populates a
/// <c>Dictionary&lt;int, BlobArenaFile&gt;</c> with one entry per referenced blob
/// arena id and hands it to the persisted snapshot. The snapshot disposes each entry
/// in its <c>CleanUp</c>. <see cref="Dispose"/> is idempotent.
/// </para>
/// </summary>
public sealed class BlobArenaFile : IDisposable
{
    private readonly IBlobArenaManager _manager;
    private readonly ushort _blobArenaId;
    // Borrowed from the manager — not owned, not disposed here. The manager keeps the
    // file open until the per-id refcount drops to zero.
    private readonly SafeFileHandle _handle;
    private int _disposed;

    internal BlobArenaFile(IBlobArenaManager manager, ushort blobArenaId, SafeFileHandle handle)
    {
        _manager = manager;
        _blobArenaId = blobArenaId;
        _handle = handle;
    }

    public ushort BlobArenaId => _blobArenaId;

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
            int read = RandomAccess.Read(_handle, destination[total..], offset + total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _manager.ReleaseBlobArena(_blobArenaId);
    }
}
