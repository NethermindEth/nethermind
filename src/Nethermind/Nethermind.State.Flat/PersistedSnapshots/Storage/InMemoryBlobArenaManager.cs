// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// RAM-backed <see cref="IBlobArenaManager"/>: trie-node RLP for a snapshot lives in its own
/// growable native buffer (<see cref="BlobArenaFile.CreateInMemory"/>) — an "in-memory blob arena
/// per snapshot" — instead of packed into on-disk blob files. Selected by
/// <c>FlatNodeStorageInMemoryArena</c>. Files are not packed: each <see cref="CreateWriter"/> takes a
/// dedicated file, and a file is freed as soon as its last snapshot lease drops. Its id is then reclaimed
/// onto a free-list and reused before a fresh one is minted, so the ushort space caps the number of
/// concurrently live base snapshots (not the cumulative count over a session). The tier is
/// session-ephemeral (nothing survives a restart, so <see cref="Initialize"/> /
/// <see cref="SweepUnreferenced"/> are no-ops).
/// </summary>
public sealed class InMemoryBlobArenaManager(long maxFileSize) : IBlobArenaManager
{
    private readonly Lock _lock = new();
    // Indexed by blob arena id (same O(1) layout as the disk pool). Null slot = no file.
    private readonly BlobArenaFile?[] _files = new BlobArenaFile?[ushort.MaxValue + 1];
    // Ids freed when a file's last lease drops, reused before minting new ones so the cap is on live
    // (not cumulative) files. Only ever touched under _lock, on the same paths that null the slot.
    private readonly Stack<ushort> _freeIds = new();
    private int _nextFileId;
    private bool _disposed;

    /// <summary>No-op: the RAM pool starts empty and holds nothing across a restart.</summary>
    public void Initialize() { }

    public BlobArenaWriter CreateWriter(long estimatedSize)
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) throw new ObjectDisposedException(nameof(InMemoryBlobArenaManager));
        // Reuse a freed id before minting a new one; the ushort space then caps the number of
        // concurrently live base snapshots (ample for the intended experimental use).
        ushort id;
        if (_freeIds.TryPop(out ushort reused))
        {
            id = reused;
        }
        else
        {
            if (_nextFileId > ushort.MaxValue)
                throw new InvalidOperationException(
                    $"In-memory blob arena id space exhausted ({ushort.MaxValue + 1} live files).");
            id = (ushort)_nextFileId++;
        }
        BlobArenaFile file = BlobArenaFile.CreateInMemory(id, maxFileSize, Math.Max(estimatedSize, 1));
        _files[id] = file;
        if (!file.TryAcquireLease())
            throw new InvalidOperationException($"In-memory blob arena {id} is mid-cleanup; cannot open writer.");
        Stream stream = file.OpenWriteStream(0);
        return new BlobArenaWriter(this, file, 0, stream);
    }

    public bool TryLeaseFile(ushort blobArenaId, [NotNullWhen(true)] out BlobArenaFile? file)
    {
        BlobArenaFile? candidate = _files[blobArenaId];
        if (candidate is null || !candidate.TryAcquireLease())
        {
            file = null;
            return false;
        }
        file = candidate;
        return true;
    }

    public BlobArenaFile GetFile(ushort blobArenaId) =>
        _files[blobArenaId]
            ?? throw new InvalidOperationException($"Blob arena {blobArenaId} not registered with this manager.");

    /// <summary>No-op: no crash orphans exist in a fresh RAM pool.</summary>
    public void SweepUnreferenced() { }

    public void OnWriteCompleted(BlobArenaFile file, bool hasHeadroom)
    {
        using Lock.Scope scope = _lock.EnterScope();
        long delta = file.Frontier - file.ReportedFrontier;
        if (delta != 0)
        {
            file.ReportedFrontier = file.Frontier;
            Interlocked.Add(ref Metrics._blobAllocatedBytes, delta);
        }
    }

    // Cancelled write on a per-snapshot file — free it (drop the manager slot lease); the writer drops
    // its own lease right after, so the last release frees the buffer.
    public void OnWriteCancelled(ushort blobArenaId)
    {
        using Lock.Scope scope = _lock.EnterScope();
        BlobArenaFile? file = _files[blobArenaId];
        if (file is null) return;
        _files[blobArenaId] = null;
        _freeIds.Push(blobArenaId);
        file.Dispose();
    }

    // A per-snapshot RAM file is never reused, so when its last snapshot lease drops just free it
    // (drop the manager slot lease) rather than resetting the frontier for packing.
    public void TryResetOrphanedFrontier(BlobArenaFile file)
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) return;
        if (_files[file.BlobArenaId] != file) return;
        if (!file.HasOnlyManagerLease) return;
        _files[file.BlobArenaId] = null;
        _freeIds.Push(file.BlobArenaId);
        file.Dispose();
    }

    public void Dispose()
    {
        using Lock.Scope scope = _lock.EnterScope();
        if (_disposed) return;
        _disposed = true;
        for (int id = 0; id < _files.Length; id++)
        {
            BlobArenaFile? file = _files[id];
            if (file is null) continue;
            _files[id] = null;
            file.Dispose();
        }
    }
}
