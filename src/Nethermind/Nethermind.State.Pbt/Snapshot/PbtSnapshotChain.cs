// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt.Snapshot;

internal sealed class PbtRetainedPublicationGate
{
    internal Lock Sync { get; } = new();
}

internal sealed class PbtRetainedStorageLifetime
{
    internal IArenaManager? Arena { get; }
    internal BlobArenaManager? Blobs { get; }
    internal PbtRetainedStorageLifetime() { }
    internal PbtRetainedStorageLifetime(IArenaManager arena, BlobArenaManager blobs) => (Arena, Blobs) = (arena, blobs);

    internal void AbortLoad()
    {
        if (Arena is ArenaManager arena) arena.PreserveFilesOnFailure();
        Blobs?.PreserveFilesOnFailure();
    }
}

internal sealed class PbtSnapshotLease : IDisposable
{
    private int _disposed;
    internal PbtSnapshot? Memory { get; }
    internal PbtRetainedSnapshot? Retained { get; }
    internal StateId From => Memory?.From ?? Retained!.From;
    internal StateId To => Memory?.To ?? Retained!.To;
    internal ValueHash256 TreeRoot => Memory?.TreeRoot ?? Retained!.TreeRoot;
    internal SnapshotTier Tier { get; }

    internal PbtSnapshotLease(PbtSnapshot snapshot, SnapshotTier tier) => (Memory, Tier) = (snapshot, tier);
    internal PbtSnapshotLease(PbtRetainedSnapshot snapshot) => (Retained, Tier) = (snapshot, snapshot.Tier);

    internal PbtSnapshotLease Lease()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Memory is { } memory)
        {
            if (!memory.TryLease()) throw new ObjectDisposedException(nameof(memory));
            return new(memory, Tier);
        }
        if (!Retained!.TryLease()) throw new ObjectDisposedException(nameof(Retained));
        return new(Retained);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Memory?.Dispose();
        Retained?.Dispose();
    }
}

internal sealed class PbtSnapshotChain : IDisposable
{
    private List<PbtSnapshotLease>? _layers;
    internal IReadOnlyList<PbtSnapshotLease> Layers => _layers ?? throw new ObjectDisposedException(nameof(PbtSnapshotChain));
    internal PbtSnapshotChain(List<PbtSnapshotLease> layers) => _layers = layers;
    public void Dispose()
    {
        List<PbtSnapshotLease>? layers = Interlocked.Exchange(ref _layers, null);
        if (layers is null) return;
        foreach (PbtSnapshotLease layer in layers) layer.Dispose();
    }
}
