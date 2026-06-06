// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// Stores trie-node RLP bytes back-to-back in its own files, separate from the
/// metadata HSST arena files held by <see cref="IArenaManager"/>. A
/// <see cref="NodeRef"/> embedded in a persisted snapshot's metadata points at
/// <c>(BlobArenaId, file-absolute offset)</c>; the manager resolves the id to the
/// underlying arena file.
///
/// <para>
/// One id per file: a <c>BlobArenaId</c> is the file's stable numeric id. Many writers
/// across many base snapshots append into the same file. Files are read through a
/// read-only mmap whose resident working set is bounded by a
/// <see cref="PageResidencyTracker"/>; snapshots lease a file and the file is deleted when
/// the last lease is released.
/// </para>
/// </summary>
public interface IBlobArenaManager : IDisposable
{
    /// <summary>
    /// Rehydrate the underlying file pool from on disk; each file restores its frontier from
    /// its own 8-byte header. Must run before any <c>PersistedSnapshot</c> is constructed so
    /// <see cref="TryLeaseFile"/> can resolve the ids stored in their <c>ref_ids</c> metadata.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Record a single OS-page access by a reader of blob file <paramref name="blobArenaId"/>
    /// against the page-residency tracker. No-op when the manager has no tracker. The caller
    /// must hold a lease on the file for the duration of the call.
    /// </summary>
    void TouchBlobPage(int blobArenaId, int pageIdx);

    /// <summary>
    /// Forget every page-residency-tracker entry whose OS page is fully covered by
    /// <c>[byteOffset, byteOffset + byteSize)</c> of blob file <paramref name="arenaId"/>.
    /// Paired with a whole-range <c>madvise(MADV_DONTNEED)</c> at the call site.
    /// </summary>
    void ForgetTrackerRange(int arenaId, long byteOffset, long byteSize);

    /// <summary>
    /// Open a writer that appends RLP items into a blob arena file (either
    /// an existing one with headroom, or a fresh one).
    /// </summary>
    BlobArenaWriter CreateWriter(long estimatedSize);

    /// <summary>
    /// Acquire a lease on the file identified by <paramref name="blobArenaId"/>. Returns
    /// false if the manager doesn't know the id, or if the file is mid-cleanup. The
    /// caller drops the lease by calling <see cref="BlobArenaFile.Dispose"/>.
    /// </summary>
    bool TryLeaseFile(ushort blobArenaId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BlobArenaFile? file);

    /// <summary>
    /// Return the blob arena file currently registered under <paramref name="blobArenaId"/>,
    /// or throw if no slot is populated. Lock-free O(1) array read — the caller MUST already
    /// hold a lease on the file (typically acquired via <see cref="TryLeaseFile"/> at snapshot
    /// load time). Does NOT bump the refcount; used by the hot read path in
    /// <see cref="PersistedSnapshots.PersistedSnapshot"/> and by the snapshot's teardown to
    /// resolve ids it leased earlier without re-paying the lease-acquisition lock.
    /// </summary>
    BlobArenaFile GetFile(ushort blobArenaId);

    /// <summary>
    /// After <see cref="Initialize"/> + snapshot rehydration, delete any arena file
    /// not referenced by a loaded snapshot — recoverable orphans from a mid-write
    /// crash where Complete never ran.
    /// </summary>
    void SweepUnreferenced();

    /// <summary>
    /// Called by <see cref="PersistedSnapshots.PersistedSnapshot.CleanUp"/> after it has
    /// released its lease on a blob file. If only the manager's slot lease remains and
    /// the file's frontier is non-zero, reset the frontier to 0 so the bytes gauge drops
    /// and the file is reusable for packing from offset 0. No-op when the file still
    /// has external lessees, or when called against the null manager.
    /// </summary>
    void TryResetOrphanedFrontier(BlobArenaFile file);
}
