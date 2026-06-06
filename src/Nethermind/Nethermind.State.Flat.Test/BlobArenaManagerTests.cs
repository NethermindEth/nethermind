// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Tests for the mmap-backed <see cref="BlobArenaManager"/>: the 8-byte frontier header that
/// survives a restart, the orphan-frontier reset that punches the data range (without truncating
/// the pre-extended mapping), and the per-manager MPSC eviction queue duplicated from
/// <see cref="ArenaManager"/>. Uses the manager's internal counters / Frontier for observability.
/// </summary>
public class BlobArenaManagerTests
{
    private const long MaxFileSize = 1L * 1024 * 1024;
    private string _testDir = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nethermind_blobmgr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met within timeout");
            Thread.Sleep(5);
        }
    }

    private BlobArenaManager NewManager(long pageCacheBytes = 0) =>
        new(_testDir, MaxFileSize, PersistedSnapshotTier.Persisted, pageCacheBytes);

    private static byte[] Rlp(int length, byte fill)
    {
        byte[] b = new byte[length];
        Array.Fill(b, fill);
        return b;
    }

    [Test]
    public void Frontier_PersistsInHeader_AcrossRestart()
    {
        ushort id;
        long expectedFrontier;
        using (BlobArenaManager m = NewManager())
        {
            m.Initialize();
            using BlobArenaWriter w = m.CreateWriter(estimatedSize: 4096);
            id = w.BlobArenaId;
            // Fresh file: the first write must land past the 8-byte frontier header.
            Assert.That(w.StartOffset, Is.EqualTo(BlobArenaFile.HeaderSize));
            w.WriteRlp(Rlp(100, 0xAB));
            w.WriteRlp(Rlp(200, 0xCD));
            w.Complete();
            w.Fsync();
            expectedFrontier = w.Written;
            // Preserve the file across the simulated restart (production does this via
            // PersistedSnapshot.PersistOnShutdown for every still-referenced blob).
            m.GetFile(id).PersistOnShutdown();
        }

        using BlobArenaManager m2 = NewManager();
        m2.Initialize();
        BlobArenaFile f = m2.GetFile(id);
        Assert.That(f.Frontier, Is.EqualTo(expectedFrontier), "frontier restored from header");
        // The file is a packing candidate: a new writer resumes exactly at the restored frontier.
        using BlobArenaWriter w2 = m2.CreateWriter(estimatedSize: 4096);
        Assert.That(w2.BlobArenaId, Is.EqualTo(id));
        Assert.That(w2.StartOffset, Is.EqualTo(expectedFrontier));
    }

    [Test]
    public void TryResetOrphanedFrontier_PunchesDataRange_KeepsMappingAndResetsHeader()
    {
        ushort id;
        using BlobArenaManager m = NewManager();
        m.Initialize();
        using (BlobArenaWriter w = m.CreateWriter(estimatedSize: 64 * 1024))
        {
            id = w.BlobArenaId;
            w.WriteRlp(Rlp(50_000, 0x7F));
            w.Complete();
            w.Fsync();
        }

        BlobArenaFile file = m.GetFile(id);
        Assert.That(file.HasOnlyManagerLease, Is.True);
        Assert.That(file.Frontier, Is.GreaterThan(BlobArenaFile.HeaderSize));

        m.TryResetOrphanedFrontier(file);

        Assert.That(file.Frontier, Is.EqualTo((long)BlobArenaFile.HeaderSize), "frontier reset to header");
        // The mapping is fixed-size: the reset punches a hole but must NOT truncate the file,
        // or the live mmap would fault past EOF (SIGBUS).
        string path = Path.Combine(_testDir, "blob_0000.bin");
        Assert.That(new FileInfo(path).Length, Is.EqualTo(MaxFileSize), "file stays pre-extended");

        // The reset is durable: a restart restores an empty (reusable) file.
        file.PersistOnShutdown();
        m.Dispose();
        using BlobArenaManager m2 = NewManager();
        m2.Initialize();
        Assert.That(m2.GetFile(id).Frontier, Is.EqualTo((long)BlobArenaFile.HeaderSize));
    }

    [Test]
    public void DisabledTracker_TouchAndQueueAreNoOps()
    {
        using BlobArenaManager m = NewManager(pageCacheBytes: 0);
        Assert.That(m.PageTracker.MaxCapacity, Is.EqualTo(0));
        m.TouchBlobPage(0, 0);
        m.QueueEviction(0, 0);
        Assert.That(m.EvictionsQueued, Is.EqualTo(0));
        Assert.That(m.EvictionsDispatched, Is.EqualTo(0));
        Assert.That(m.EvictionsInlineFallback, Is.EqualTo(0));
    }

    [Test]
    public void QueueEviction_EnqueuesAndDrains_AndSkipsRetouchedPages()
    {
        long budget = 1024L * Environment.SystemPageSize;
        using BlobArenaManager m = NewManager(budget);

        // (1) A stale id is a clean dispatch (DispatchEvictionInline no-ops on the null slot).
        m.QueueEviction(arenaId: 42, pageIdx: 3);
        WaitFor(() => m.EvictionsDispatched == 1);
        Assert.That(m.EvictionsQueued, Is.EqualTo(1));
        Assert.That(m.EvictionsInlineFallback, Is.EqualTo(0));

        // (2) A page back in the tracker before drain is skipped, not dispatched.
        m.PageTracker.TryTouch(42, 7, out _, out _);
        m.QueueEviction(arenaId: 42, pageIdx: 7);
        WaitFor(() => m.EvictionsSkippedRetouched == 1);
        Assert.That(m.EvictionsDispatched, Is.EqualTo(1));
    }

    [Test]
    public void Dispose_DrainsRemainingEvictions()
    {
        long budget = 1024L * Environment.SystemPageSize;
        BlobArenaManager m = NewManager(budget);

        const int batch = 16;
        for (int i = 0; i < batch; i++)
            m.QueueEviction(arenaId: 42, pageIdx: i);

        m.Dispose();
        Assert.That(m.EvictionsQueued, Is.EqualTo(batch));
        Assert.That(
            m.EvictionsDispatched + m.EvictionsSkippedRetouched,
            Is.EqualTo(m.EvictionsQueued + m.EvictionsInlineFallback));
    }
}
