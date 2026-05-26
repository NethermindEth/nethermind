// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using FluentAssertions;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NonBlocking;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Per-tier arena / blob allocated-bytes gauges. Verifies that the metric reflects
/// <c>Frontier</c> (bytes actually written), not the pre-extended sparse mmap size, and
/// that arena vs blob files surface in distinct gauges.
/// </summary>
[TestFixture]
public class ArenaMetricsTests
{
    private string _testDir = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nm_arena_metrics_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best-effort */ }
    }

    private static long Read(ConcurrentDictionary<PersistedSnapshotTier, long> gauge, PersistedSnapshotTier tier) =>
        gauge.TryGetValue(tier, out long v) ? v : 0L;

    [Test]
    public void ArenaWriter_Complete_AdvancesAllocatedBytes_ByFrontierDelta_NotMappedSize()
    {
        // Use a per-tier delta so parallel-running tests with the same tier don't interfere.
        PersistedSnapshotTier tier = PersistedSnapshotTier.Persisted;
        const long maxArenaSize = 64 * 1024;  // 64 KiB sparse arena file
        const int payloadBytes = 4096;        // write 4 KiB into it

        long arenaBytesBefore = Read(Metrics.ArenaAllocatedBytesByTier, tier);
        long arenaCountBefore = Read(Metrics.ArenaFileCountByTier, tier);
        long blobBytesBefore = Read(Metrics.BlobAllocatedBytesByTier, tier);
        long blobCountBefore = Read(Metrics.BlobFileCountByTier, tier);
        long resvBytesBefore = Read(Metrics.ArenaReservationBytesByTier, tier);

        string arenaDir = Path.Combine(_testDir, "arena");
        using ArenaManager arena = new(arenaDir, pageCacheBytes: 0,
            maxArenaSize: maxArenaSize, tier: tier);

        // Before any write the file isn't materialised yet (CreateArenaFile fires on first writer).
        Read(Metrics.ArenaAllocatedBytesByTier, tier).Should().Be(arenaBytesBefore);
        Read(Metrics.ArenaFileCountByTier, tier).Should().Be(arenaCountBefore);

        ArenaReservation reservation;
        using (ArenaWriter writer = arena.CreateWriter(payloadBytes))
        {
            // File materialised — count +1, allocated bytes still 0 (frontier == 0 at open).
            Read(Metrics.ArenaFileCountByTier, tier).Should().Be(arenaCountBefore + 1);
            Read(Metrics.ArenaAllocatedBytesByTier, tier).Should().Be(arenaBytesBefore);

            ref ArenaBufferWriter buf = ref writer.GetWriter();
            buf.GetSpan(payloadBytes).Clear();
            buf.Advance(payloadBytes);
            (_, reservation) = writer.Complete();
        }

        // After Complete the frontier delta lands in ArenaAllocatedBytesByTier — exactly the
        // payload size, NOT the 64 KiB sparse MaxSize.
        (Read(Metrics.ArenaAllocatedBytesByTier, tier) - arenaBytesBefore).Should().Be(payloadBytes);

        // Reservation gauge tracks the live reservation we're holding.
        (Read(Metrics.ArenaReservationBytesByTier, tier) - resvBytesBefore).Should().Be(payloadBytes);

        // Arena and blob gauges are independent — no blob activity here.
        Read(Metrics.BlobAllocatedBytesByTier, tier).Should().Be(blobBytesBefore);
        Read(Metrics.BlobFileCountByTier, tier).Should().Be(blobCountBefore);

        // Dropping the reservation marks all its bytes dead → MarkDead drops the file →
        // OnArenaRemoved returns the count and allocated-bytes contributions to baseline.
        reservation.Dispose();
        Read(Metrics.ArenaReservationBytesByTier, tier).Should().Be(resvBytesBefore);
        Read(Metrics.ArenaFileCountByTier, tier).Should().Be(arenaCountBefore);
        Read(Metrics.ArenaAllocatedBytesByTier, tier).Should().Be(arenaBytesBefore);
    }

    [Test]
    public void BlobArenaWriter_Complete_AdvancesBlobAllocatedBytes_AndKeepsArenaGaugeAtZero()
    {
        PersistedSnapshotTier tier = PersistedSnapshotTier.Persisted;
        const long maxFileSize = 64 * 1024;
        const int blobBytes = 1024;

        long arenaBytesBefore = Read(Metrics.ArenaAllocatedBytesByTier, tier);
        long arenaCountBefore = Read(Metrics.ArenaFileCountByTier, tier);
        long blobBytesBefore = Read(Metrics.BlobAllocatedBytesByTier, tier);
        long blobCountBefore = Read(Metrics.BlobFileCountByTier, tier);

        string blobDir = Path.Combine(_testDir, "blob");
        using BlobArenaManager blobs = new(blobDir, maxFileSize, tier);

        using (BlobArenaWriter writer = blobs.CreateWriter(blobBytes))
        {
            // File materialised on first writer — count +1, allocated still 0.
            Read(Metrics.BlobFileCountByTier, tier).Should().Be(blobCountBefore + 1);
            Read(Metrics.BlobAllocatedBytesByTier, tier).Should().Be(blobBytesBefore);

            byte[] rlp = new byte[blobBytes];
            writer.WriteRlp(rlp);
            writer.Complete();
        }

        // After Complete: blob allocated bytes advance by exactly the written size (not the
        // 64 KiB MaxSize of the sparse file).
        (Read(Metrics.BlobAllocatedBytesByTier, tier) - blobBytesBefore).Should().Be(blobBytes);

        // Arena gauges stay flat — blob writes never touch them.
        Read(Metrics.ArenaAllocatedBytesByTier, tier).Should().Be(arenaBytesBefore);
        Read(Metrics.ArenaFileCountByTier, tier).Should().Be(arenaCountBefore);
    }
}
