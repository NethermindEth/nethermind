// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Win32.SafeHandles;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A blob arena file storing trie-node RLP bytes. Owns its <see cref="SafeFileHandle"/>
/// and is refcounted: the owning <see cref="BlobArenaManager"/>'s dictionary entry holds
/// the initial lease, each leased <see cref="PersistedSnapshots.PersistedSnapshot"/> holds
/// an additional one. The manager drops its lease via <see cref="RefCountingDisposable.Dispose"/>;
/// the on-disk file is deleted by <see cref="CleanUp"/> when the last lease is released,
/// unless the manager is in shutdown — in which case the file is preserved for the next
/// session.
///
/// <para>
/// Reads use <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/> directly:
/// no mmap, no page tracker, no advise — the blob path is pure <c>pread</c>.
/// </para>
/// </summary>
public sealed class BlobArenaFile : RefCountingDisposable
{
    private readonly BlobArenaManager _manager;

    /// <summary>Stable file id, narrowed from int to ushort. Embedded in every <see cref="NodeRef"/>.</summary>
    public ushort BlobArenaId { get; }

    /// <summary>On-disk path. Deleted by <see cref="CleanUp"/> unless the manager is in shutdown.</summary>
    public string Path { get; }

    /// <summary>Pre-extended file length (sparse on Linux). Writers append within this cap.</summary>
    public long MaxSize { get; }

    /// <summary>Underlying read/write file handle. Borrowed by leases for direct <c>pread</c>.</summary>
    internal SafeFileHandle Handle { get; }

    /// <summary>Next-write offset. Mutated under the manager's lock during writer registration.</summary>
    internal long Frontier { get; set; }

    internal BlobArenaFile(BlobArenaManager manager, ushort id, string path, long maxSize, long frontier)
    {
        _manager = manager;
        BlobArenaId = id;
        Path = path;
        MaxSize = maxSize;
        Handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        // Pre-extend file to MaxSize if smaller (sparse on Linux via ftruncate). Subsequent
        // appends never have to grow the file.
        if (RandomAccess.GetLength(Handle) < maxSize)
            RandomAccess.SetLength(Handle, maxSize);
        Frontier = frontier;
    }

    /// <summary>
    /// Defensive lease acquisition; returns false when the file has already entered
    /// <see cref="CleanUp"/>. Promotes <see cref="RefCountingDisposable.TryAcquireLease"/>
    /// from protected to internal so the owning manager can lease under its lock.
    /// </summary>
    internal new bool TryAcquireLease() => base.TryAcquireLease();

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

    protected override void CleanUp()
    {
        Handle.Dispose();
        // Shutdown preserves files for the next session — skip the on-disk delete.
        if (!_manager.IsDisposed)
        {
            try { File.Delete(Path); } catch { /* best-effort */ }
        }
    }
}
