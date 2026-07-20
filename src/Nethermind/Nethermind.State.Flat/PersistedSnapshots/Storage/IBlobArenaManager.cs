// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// File pool for trie-node RLP bytes: writers append RLP through a <see cref="BlobArenaWriter"/>
/// and loaded snapshots resolve their referenced <see cref="BlobArenaFile"/>s by id. The disk
/// implementation is <see cref="BlobArenaManager"/> (one packed pool of on-disk files);
/// <see cref="InMemoryBlobArenaManager"/> is the RAM-backed, one-file-per-snapshot variant.
/// </summary>
public interface IBlobArenaManager : IDisposable
{
    /// <summary>Rehydrate the file pool before any snapshot is constructed. No-op for the RAM pool.</summary>
    void Initialize();

    /// <summary>Open a writer that appends RLP into an arena file with headroom (or a fresh one).</summary>
    BlobArenaWriter CreateWriter(long estimatedSize);

    /// <summary>Acquire a lease on the file identified by <paramref name="blobArenaId"/>.</summary>
    bool TryLeaseFile(ushort blobArenaId, [NotNullWhen(true)] out BlobArenaFile? file);

    /// <summary>Return the registered file for <paramref name="blobArenaId"/> (caller already holds a lease).</summary>
    BlobArenaFile GetFile(ushort blobArenaId);

    /// <summary>Delete arena files no loaded snapshot referenced after a restart. No-op for the RAM pool.</summary>
    void SweepUnreferenced();

    /// <summary>Reclaim a file whose last snapshot lease just dropped (reset frontier / free).</summary>
    void TryResetOrphanedFrontier(BlobArenaFile file);

    /// <summary>Post-<see cref="BlobArenaWriter.Complete"/> bookkeeping. Called by the writer.</summary>
    void OnWriteCompleted(BlobArenaFile file, bool hasHeadroom);

    /// <summary>Bookkeeping after a cancelled write. Called by the writer.</summary>
    void OnWriteCancelled(ushort blobArenaId);
}
