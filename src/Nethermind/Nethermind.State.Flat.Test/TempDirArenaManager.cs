// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Test-only convenience wrapper over <see cref="ArenaManager"/> backed by a fresh
/// per-instance temporary directory. Provides the same surface as the production
/// manager so tests can drop it in without further setup: disposing this wrapper
/// closes the inner manager and recursively deletes the tempdir. Page tracker is
/// disabled (no madvise / eviction queue) so tests stay deterministic and
/// side-effect free.
/// </summary>
public sealed class TempDirArenaManager : IArenaManager
{
    private readonly string _tempDir;
    private readonly ArenaManager _inner;

    public TempDirArenaManager(int arenaSize = 64 * 1024)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nm-temparena-" + Guid.NewGuid().ToString("N"));
        // ArenaFile requires the mmap to be page-aligned; 4 KiB floor avoids tiny test sizes
        // tripping the mmap minimum.
        long maxArenaSize = Math.Max(arenaSize, Environment.SystemPageSize);
        _inner = new ArenaManager(_tempDir, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
            ArenaFileSizeBytes = maxArenaSize,
        }, LimboLogs.Instance);
    }

    public PageResidencyTracker PageTracker => _inner.PageTracker;

    public void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries) => _inner.Initialize(entries);

    public ArenaWriter CreateWriter(long estimatedSize) => _inner.CreateWriter(estimatedSize);

    public ArenaReservation Open(in SnapshotLocation location) => _inner.Open(location);

    public void QueueEviction(int arenaId, int pageIdx) => _inner.QueueEviction(arenaId, pageIdx);

    public bool MarkDead(ArenaFile file, long deadSize) => _inner.MarkDead(file, deadSize);

    public bool TryPunchHole(ArenaFile file, long offset, long size) => _inner.TryPunchHole(file, offset, size);

    public void ForgetTrackerRange(int arenaId, long byteOffset, long byteSize) =>
        _inner.ForgetTrackerRange(arenaId, byteOffset, byteSize);


    public void Dispose()
    {
        _inner.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
