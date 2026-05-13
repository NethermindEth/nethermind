// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Test-only convenience wrapper over <see cref="ArenaManager"/> backed by a fresh
/// per-instance temporary directory. Provides the same surface as the production
/// manager so existing tests and benchmarks can drop it in without further setup:
/// disposing this wrapper closes the inner manager and recursively deletes the
/// tempdir. Page tracker is disabled (no madvise / eviction queue) so tests stay
/// deterministic and side-effect free.
/// </summary>
public sealed class MemoryArenaManager : IArenaManager
{
    private readonly string _tempDir;
    private readonly ArenaManager _inner;

    public MemoryArenaManager(int arenaSize = 64 * 1024)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nm-memarena-" + Guid.NewGuid().ToString("N"));
        // ArenaFile requires the mmap to be page-aligned; 4 KiB floor avoids tiny test sizes
        // tripping the mmap minimum.
        long maxArenaSize = Math.Max(arenaSize, Environment.SystemPageSize);
        _inner = new ArenaManager(_tempDir, pageCacheBytes: 0, maxArenaSize: maxArenaSize);
    }

    public PageResidencyTracker PageTracker => _inner.PageTracker;

    public void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries) => _inner.Initialize(entries);

    public ArenaWriter CreateWriter(long estimatedSize, string tag) => _inner.CreateWriter(estimatedSize, tag);

    public (SnapshotLocation Location, ArenaReservation Reservation) CompleteWrite(int arenaId, long startOffset, long actualSize, string tag) =>
        _inner.CompleteWrite(arenaId, startOffset, actualSize, tag);

    public void CancelWrite(int arenaId, long startOffset) => _inner.CancelWrite(arenaId, startOffset);

    public ArenaReservation Open(in SnapshotLocation location, string tag) => _inner.Open(location, tag);

    public IArenaWholeView OpenPendingView(int arenaId, long absoluteOffset, long size) =>
        _inner.OpenPendingView(arenaId, absoluteOffset, size);

    public void AdviseDontNeed(ArenaReservation reservation) => _inner.AdviseDontNeed(reservation);

    public void QueueEviction(int arenaId, int pageIdx) => _inner.QueueEviction(arenaId, pageIdx);

    public void MarkDead(in SnapshotLocation location) => _inner.MarkDead(location);

    public void Dispose()
    {
        _inner.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
