// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Arena / blob allocated-bytes gauges. Verifies that the metric reflects
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

    [Test]
    public void ArenaWriter_Complete_AdvancesAllocatedBytes_ByFrontierDelta_NotMappedSize()
    {
        // Use a delta from the baseline so parallel-running tests don't interfere.
        const long maxArenaSize = 64 * 1024;  // 64 KiB sparse arena file
        const int payloadBytes = 4096;

        long arenaBytesBefore = Metrics.ArenaAllocatedBytes;
        long arenaCountBefore = Metrics.ArenaFileCount;
        long blobBytesBefore = Metrics.BlobAllocatedBytes;
        long blobCountBefore = Metrics.BlobFileCount;
        long resvBytesBefore = Metrics.ArenaReservationBytes;

        string arenaDir = Path.Combine(_testDir, "arena");
        using ArenaManager arena = new(arenaDir, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
            ArenaFileSizeBytes = maxArenaSize,
        }, LimboLogs.Instance);

        // Before any write the file isn't materialised yet (CreateArenaFile fires on first writer).
        Assert.That(Metrics.ArenaAllocatedBytes, Is.EqualTo(arenaBytesBefore));
        Assert.That(Metrics.ArenaFileCount, Is.EqualTo(arenaCountBefore));

        ArenaReservation reservation;
        using (ArenaWriter writer = arena.CreateWriter(payloadBytes))
        {
            // File materialised — count +1, allocated bytes still 0 (frontier == 0 at open).
            Assert.That(Metrics.ArenaFileCount, Is.EqualTo(arenaCountBefore + 1));
            Assert.That(Metrics.ArenaAllocatedBytes, Is.EqualTo(arenaBytesBefore));

            ref ArenaBufferWriter buf = ref writer.GetWriter();
            buf.GetSpan(payloadBytes).Clear();
            buf.Advance(payloadBytes);
            (_, reservation) = writer.Complete();
        }

        // After Complete the frontier delta lands in ArenaAllocatedBytes — exactly the
        // payload size, NOT the 64 KiB sparse MaxSize.
        Assert.That((Metrics.ArenaAllocatedBytes - arenaBytesBefore), Is.EqualTo(payloadBytes));

        Assert.That((Metrics.ArenaReservationBytes - resvBytesBefore), Is.EqualTo(payloadBytes));

        // Arena and blob gauges are independent — no blob activity here.
        Assert.That(Metrics.BlobAllocatedBytes, Is.EqualTo(blobBytesBefore));
        Assert.That(Metrics.BlobFileCount, Is.EqualTo(blobCountBefore));

        // Dropping the reservation marks all its bytes dead → MarkDead drops the file →
        // OnArenaRemoved returns the count and allocated-bytes contributions to baseline.
        reservation.Dispose();
        Assert.That(Metrics.ArenaReservationBytes, Is.EqualTo(resvBytesBefore));
        Assert.That(Metrics.ArenaFileCount, Is.EqualTo(arenaCountBefore));
        Assert.That(Metrics.ArenaAllocatedBytes, Is.EqualTo(arenaBytesBefore));
    }

    [Test]
    public void BlobArenaWriter_Complete_AdvancesBlobAllocatedBytes_AndKeepsArenaGaugeAtZero()
    {
        const long maxFileSize = 64 * 1024;
        const int blobBytes = 1024;

        long arenaBytesBefore = Metrics.ArenaAllocatedBytes;
        long arenaCountBefore = Metrics.ArenaFileCount;
        long blobBytesBefore = Metrics.BlobAllocatedBytes;
        long blobCountBefore = Metrics.BlobFileCount;

        string blobDir = Path.Combine(_testDir, "blob");
        using BlobArenaManager blobs = new(blobDir, maxFileSize);

        using (BlobArenaWriter writer = blobs.CreateWriter(blobBytes))
        {
            // File materialised on first writer — count +1, allocated still 0.
            Assert.That(Metrics.BlobFileCount, Is.EqualTo(blobCountBefore + 1));
            Assert.That(Metrics.BlobAllocatedBytes, Is.EqualTo(blobBytesBefore));

            byte[] rlp = new byte[blobBytes];
            writer.WriteRlp(rlp);
            writer.Complete();
        }

        // After Complete: blob allocated bytes advance by exactly the written size (not the
        // 64 KiB MaxSize of the sparse file).
        Assert.That((Metrics.BlobAllocatedBytes - blobBytesBefore), Is.EqualTo(blobBytes));

        // Arena gauges stay flat — blob writes never touch them.
        Assert.That(Metrics.ArenaAllocatedBytes, Is.EqualTo(arenaBytesBefore));
        Assert.That(Metrics.ArenaFileCount, Is.EqualTo(arenaCountBefore));
    }
}
