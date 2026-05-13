// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Stores trie-node RLP bytes back-to-back in its own files, separate from the
/// metadata HSST arena files held by <see cref="IArenaManager"/>. A
/// <see cref="NodeRef"/> embedded in a persisted snapshot's metadata points at
/// <c>(BlobArenaId, file-absolute offset)</c>; the manager resolves the id to the
/// underlying arena file.
///
/// <para>
/// Wiring convention: each persisted-snapshot pool tier is a pair —
/// <c>(ArenaManager metadata, BlobArenaManager blobs)</c>. There are two such pairs,
/// Small (short-range, <c>To-From &lt; CompactSize</c>) and Large (everything else),
/// instantiated side-by-side in <c>FlatWorldStateModule</c>. BlobArenaManager itself
/// is not pool-aware — a caller picks which instance to talk to.
/// </para>
///
/// <para>
/// One id per file: a <c>BlobArenaId</c> is the underlying <c>ArenaFile.Id</c>.
/// Many writers across many base snapshots append into the same file. The
/// manager maintains one whole-file <see cref="ArenaReservation"/> per known
/// id; snapshots lease the reservation, and the file is deleted when the last
/// snapshot releases it.
/// </para>
/// </summary>
public interface IBlobArenaManager : IDisposable
{
    /// <summary>
    /// Rehydrate the underlying file pool from on-disk file lengths. Whole-file
    /// reservations are created lazily on first <see cref="TryLeaseFile"/>. Must
    /// run before any <c>PersistedSnapshot</c> is constructed.
    /// </summary>
    void Initialize();

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
    /// After <see cref="Initialize"/> + snapshot rehydration, delete any arena file
    /// not referenced by a loaded snapshot — recoverable orphans from a mid-write
    /// crash where Complete never ran.
    /// </summary>
    void SweepUnreferenced();
}
