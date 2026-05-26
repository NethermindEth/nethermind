// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Verifies that dead persisted-snapshot arena ranges have their disk blocks reclaimed via
/// <c>fallocate(FALLOC_FL_PUNCH_HOLE)</c> — on metadata-reservation cleanup and on blob-file
/// frontier reset — and that the <c>PersistedSnapshotPunchHoleOnReclaim</c> flag gates it.
/// Linux-only; gracefully ignored when the temp filesystem does not support hole-punching.
/// </summary>
[TestFixture]
public class ArenaReclaimPunchHoleTests
{
    private string _testDir = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nm_punchhole_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best-effort */ }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ReservationCleanup_PunchesHole_ForDeadRange_WhenEnabled(bool punchHoleOnReclaim)
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("fallocate punch-hole is Linux-only");
        int pageSize = Environment.SystemPageSize;
        string arenaDir = Path.Combine(_testDir, "arena");

        using ArenaManager manager = new(arenaDir, pageCacheBytes: 0,
            maxArenaSize: 8L * 1024 * 1024, tier: PersistedSnapshotTier.Persisted,
            punchHoleOnReclaim: punchHoleOnReclaim);

        // Two reservations in one shared arena file: disposing the first leaves the file
        // alive (the second keeps DeadBytes < Frontier), so cleanup actually punches.
        (SnapshotLocation locA, ArenaReservation reservationA) = WriteReservation(manager, 64 * pageSize);
        (SnapshotLocation locB, ArenaReservation reservationB) = WriteReservation(manager, pageSize);
        locA.ArenaId.Should().Be(locB.ArenaId, "both writes must pack into the same shared arena file");

        string arenaPath = Directory.GetFiles(arenaDir).Single();
        Fsync(arenaPath);
        long blocksBefore = StatBlocks(arenaPath);
        blocksBefore.Should().BeGreaterThan(0, "the written reservations should occupy real disk blocks");

        reservationA.Dispose();

        if (punchHoleOnReclaim && !manager.PunchHoleSupported)
            Assert.Ignore("filesystem does not support fallocate punch-hole");

        long blocksAfter = StatBlocks(arenaPath);
        if (punchHoleOnReclaim)
            blocksAfter.Should().BeLessThan(blocksBefore, "cleanup should punch-hole reservation A's dead range");
        else
            blocksAfter.Should().Be(blocksBefore, "punch-hole is disabled");

        reservationB.Dispose();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void BlobFrontierReset_PunchesHole_ForOrphanedRange_WhenEnabled(bool punchHoleOnReclaim)
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("fallocate punch-hole is Linux-only");
        const int rlpSize = 4096;
        const int rlpCount = 64;
        string blobDir = Path.Combine(_testDir, "blob");

        using BlobArenaManager blobs = new(blobDir, 8L * 1024 * 1024,
            PersistedSnapshotTier.Persisted, punchHoleOnReclaim: punchHoleOnReclaim);

        ushort blobId;
        using (BlobArenaWriter writer = blobs.CreateWriter(rlpSize * rlpCount))
        {
            byte[] rlp = new byte[rlpSize];
            for (int i = 0; i < rlpCount; i++)
            {
                Random.Shared.NextBytes(rlp);
                writer.WriteRlp(rlp);
            }
            writer.Complete();
            blobId = writer.BlobArenaId;
        }

        string blobPath = Directory.GetFiles(blobDir).Single();
        Fsync(blobPath);
        long blocksBefore = StatBlocks(blobPath);
        blocksBefore.Should().BeGreaterThan(0, "the written blobs should occupy real disk blocks");

        // The writer's lease is gone, so the file is orphaned — frontier reset recycles it.
        BlobArenaFile file = blobs.GetFile(blobId);
        blobs.TryResetOrphanedFrontier(file);
        file.Frontier.Should().Be(0, "frontier reset runs regardless of punch-hole support");

        if (punchHoleOnReclaim && !blobs.PunchHoleSupported)
            Assert.Ignore("filesystem does not support fallocate punch-hole");

        long blocksAfter = StatBlocks(blobPath);
        if (punchHoleOnReclaim)
            blocksAfter.Should().BeLessThan(blocksBefore, "frontier reset should punch-hole the orphaned range");
        else
            blocksAfter.Should().Be(blocksBefore, "punch-hole is disabled");
    }

    private static (SnapshotLocation, ArenaReservation) WriteReservation(ArenaManager manager, int size)
    {
        using ArenaWriter writer = manager.CreateWriter(size);
        ref ArenaBufferWriter buf = ref writer.GetWriter();
        int remaining = size;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, 64 * 1024);
            Random.Shared.NextBytes(buf.GetSpan(chunk)[..chunk]);
            buf.Advance(chunk);
            remaining -= chunk;
        }
        return writer.Complete();
    }

    // Force the OS page cache to disk so st_blocks reflects the written data before the
    // punch — ext4 delayed allocation otherwise leaves freshly-written blocks uncounted.
    private static void Fsync(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Flush(flushToDisk: true);
    }

    // .NET exposes no st_blocks accessor; shell out to coreutils stat (512-byte block count).
    private static long StatBlocks(string path)
    {
        ProcessStartInfo psi = new() { FileName = "stat", RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("%b");
        psi.ArgumentList.Add(path);
        using Process proc = Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return long.Parse(output);
    }
}
