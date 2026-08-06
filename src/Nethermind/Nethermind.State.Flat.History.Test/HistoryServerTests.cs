// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryServerTests
{
    private static readonly Address[] Addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE];
    private const int NoEntryCap = 1_000_000;
    private const int NoChunkCap = 1_000_000;

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp() => _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

    [TearDown]
    public void TearDown() => _historyColumns.Dispose();

    private HistoryServer CreateServer(FlatDbConfig config)
    {
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        return new HistoryServer(_historyColumns, config, availability, rowFormat);
    }

    [Test]
    public void CanServe_WhenHistoryDisabled_ReportsFalse()
    {
        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = false });

        Assert.That(server.CanServe, Is.False, "a node that never captures history has nothing to serve");
    }

    [Test]
    public void ServedScopes_WhenNoWatermarkPublished_IsEmpty()
    {
        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        Assert.That(server.ServedScopes, Is.Empty, "no captured block means no servable scope yet");
    }

    [Test]
    public void ServedScopes_ReflectsPublishedWatermarkAndFloor()
    {
        HistoryColumnsWriter.SetWatermark(_historyColumns, 100);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 20);
        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        IReadOnlyList<HistoryServingScope> scopes = server.ServedScopes;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scopes.Count, Is.EqualTo(1), "the general window is exactly one all-keys scope record");
            Assert.That(scopes[0].FloorBlock, Is.EqualTo(20UL), "the served floor must match the published retention floor");
            Assert.That(scopes[0].WatermarkBlock, Is.EqualTo(100UL), "the served watermark must match the published contiguous watermark");
        }
    }

    [Test]
    public void GetHistoryRangeAtHeight_response_never_exceeds_soft_cap_and_cursor_resumes_correctly()
    {
        for (int i = 0; i < Addresses.Length; i++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, Addresses[i], block: 1, new Account((ulong)i + 1, (ulong)(i + 1) * 100));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRangeEntry> firstPage, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 1, cursor: null, byteLimit: 1, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstPage.Count, Is.LessThan(Addresses.Length), "a 1-byte soft cap must not let every address through in one response");
            Assert.That(cursor, Is.Not.Null, "a capped response must hand back a cursor for continuation");
        }

        List<HistoryRangeEntry> collected = [.. firstPage];
        firstPage.Dispose();

        int pagesRemaining = Addresses.Length + 1;
        while (cursor is not null)
        {
            Assert.That(--pagesRemaining, Is.GreaterThan(0), "the cursor must never repeat the same group forever - a broken cursor-skip would hang here instead of failing");
            (IOwnedReadOnlyList<HistoryRangeEntry> page, byte[]? next) = server.GetHistoryRangeAtHeight(
                ValueKeccak.Zero, ValueKeccak.MaxValue, height: 1, cursor, byteLimit: 1, NoEntryCap, CancellationToken.None);
            collected.AddRange(page);
            page.Dispose();
            cursor = next;
        }

        Assert.That(collected, Has.Count.EqualTo(Addresses.Length), "resuming with the returned cursor repeatedly must eventually surface every address exactly once");
    }

    [Test]
    public void GetHistoryRangeAtHeight_cursor_skips_a_genesis_shaped_block0_group_without_repeating()
    {
        for (int i = 0; i < Addresses.Length; i++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, Addresses[i], block: 0, new Account((ulong)i, (ulong)i * 100));
            HistoryColumnsWriter.RecordAccount(_historyColumns, Addresses[i], block: 10, new Account((ulong)i + 10, (ulong)(i + 10) * 100));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 10);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        List<byte[]> seenKeys = [];
        byte[]? cursor = null;
        int pagesRemaining = Addresses.Length + 2;
        do
        {
            Assert.That(--pagesRemaining, Is.GreaterThan(0), "a block-0 row's suffix is all 0xFF - the exact case the cursor-skip fix protects - so a broken fix would hang here instead of failing");
            (IOwnedReadOnlyList<HistoryRangeEntry> page, byte[]? next) = server.GetHistoryRangeAtHeight(
                ValueKeccak.Zero, ValueKeccak.MaxValue, height: 10, cursor, byteLimit: 1, NoEntryCap, CancellationToken.None);
            foreach (HistoryRangeEntry entry in page) seenKeys.Add(entry.Key);
            page.Dispose();
            cursor = next;
        } while (cursor is not null);

        Assert.That(seenKeys.Select(k => Convert.ToHexString(k)).Distinct().Count(), Is.EqualTo(Addresses.Length),
            "every address must be surfaced exactly once even though each one's newest row (block 10) sits above an older block-0 row with an all-0xFF suffix");
    }

    [Test]
    public void GetHistoryRangeAtHeight_resolves_first_version_at_or_below_height()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, TestItem.AddressA, block: 5, new Account(5, 500));
        HistoryColumnsWriter.RecordAccount(_historyColumns, TestItem.AddressA, block: 20, new Account(20, 2000));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 20);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 12, cursor: null, byteLimit: 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(1), "one key was written, so exactly one entry must resolve");
            Assert.That(entries[0].Block, Is.EqualTo(5UL), "height 12 sits between the two writes, so the block-5 version is the correct as-of answer");
            Assert.That(cursor, Is.Null, "a fully-drained scan must not hand back a continuation cursor");
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRangeAtHeight_OnWindowedV3Database_ResolvesFirstChangeAboveHeight()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 1000 };
        Account atBlock20 = new(20, 2000);
        Account atBlock5 = new(5, 500);

        // v3 rows are pre-values: the row at block 20 records what AddressA held BEFORE that change, i.e. its
        // value at block 5 (and every block up to 19); AddressB never changes again after block 5, so it has no
        // row above that at all and must resolve via the live persisted flat fallback, matching HistoryReader's
        // own contract - which HistoryServer cannot exercise without a live flat column, so this covers only the
        // has-a-later-row case explicitly (the live-fallback gap for a range scan is a separate, reported gap).
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.AddressA, block: 20, atBlock5);
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryServer server = CreateServer(config);

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 12, cursor: null, byteLimit: 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(1), "a v3 database must resolve the captured row, not decode it as v2 and silently find nothing");
            Assert.That(entries[0].Block, Is.EqualTo(20UL), "the reported block is the row actually found (the next-change block), matching HistoryStoreV3.TryGetValueBeforeNextChange's own metadata contract");
        }

        entries.Dispose();
        _ = atBlock20;
    }

    [Test]
    public void GetHistoryRangeAtHeight_WhenHeightAboveWatermark_ReturnsNothing()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, TestItem.AddressA, block: 5, new Account(5, 500));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 10);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 50, cursor: null, byteLimit: 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(0), "a height above the contiguous watermark is not yet fully captured, so it must refuse rather than guess from partial data");
            Assert.That(cursor, Is.Null);
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRangeAtHeight_WhenHeightBelowFloor_ReturnsNothing()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, TestItem.AddressA, block: 5, new Account(5, 500));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 100);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 50);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 10, cursor: null, byteLimit: 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(0), "a height below the retention floor may be answered from partially-pruned data, so it must be refused outright rather than served as if authoritative");
            Assert.That(cursor, Is.Null);
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRangeAtHeight_HeavilyModifiedKey_DoesNotWalkEveryVersionOnceAnswered()
    {
        const ulong versionCount = 5_000;
        for (ulong block = 1; block <= versionCount; block++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, TestItem.AddressA, block, new Account(block, block * 100));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, versionCount);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: versionCount, cursor: null, byteLimit: 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(1), "one key was written many times, so exactly one entry must resolve");
            Assert.That(entries[0].Block, Is.EqualTo(versionCount), "the newest version is at or below the requested height, so it is the correct floor answer");
            Assert.That(cursor, Is.Null);
        }

        entries.Dispose();
    }

    [Test]
    public async Task GetChangesets_WhenFromBlockBelowFloor_ReturnsNothing()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryChangesetSidecarEnabled = true };
        ChangesetSidecarStore sidecar = new(_historyColumns.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            sidecar.RecordChunk(10, 0, [1, 2, 3], batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 100);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 50);

        HistoryServer server = CreateServer(config);

        List<ChangesetChunkEntry> chunks = [];
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(10, 10, 1_000_000, NoChunkCap, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.That(chunks, Is.Empty, "a block below the retention floor must never be served, even if a stray sidecar chunk exists for it");
    }

    [Test]
    public async Task GetChangesets_WhenCancelledBeforeCall_ReturnsNothing()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryChangesetSidecarEnabled = true };
        ChangesetSidecarStore sidecar = new(_historyColumns.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            sidecar.RecordChunk(1, 0, [1, 2, 3], batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(config);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        List<ChangesetChunkEntry> chunks = [];
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(1, 1, 1_000_000, NoChunkCap, cts.Token))
        {
            chunks.Add(chunk);
        }

        Assert.That(chunks, Is.Empty, "a request cancelled before completion must end the stream cleanly, never throw and never report partial data as if it were the full response");
    }

    [Test]
    public async Task GetChangesets_WhenToBlockAboveWatermark_ReturnsNothing()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryChangesetSidecarEnabled = true };
        ChangesetSidecarStore sidecar = new(_historyColumns.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            sidecar.RecordChunk(50, 0, [1, 2, 3], batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 10);

        HistoryServer server = CreateServer(config);

        List<ChangesetChunkEntry> chunks = [];
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(50, 50, 1_000_000, NoChunkCap, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.That(chunks, Is.Empty, "a block above the contiguous watermark must never be served, even if a stray sidecar chunk exists for it");
    }

    [Test]
    public void GetHistoryRangeAtHeight_WhenCancelledMidScan_ReturnsValidResumeCursorNotNull()
    {
        for (int i = 0; i < Addresses.Length; i++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, Addresses[i], block: 1, new Account((ulong)i + 1, (ulong)(i + 1) * 100));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRangeEntry> firstPage, byte[]? firstCursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 1, cursor: null, byteLimit: 1, NoEntryCap, CancellationToken.None);
        Assert.That(firstCursor, Is.Not.Null, "precondition: the first capped page must hand back a resumable cursor");
        firstPage.Dispose();

        using CancellationTokenSource cts = new();
        cts.Cancel();
        (IOwnedReadOnlyList<HistoryRangeEntry> cancelledPage, byte[]? cancelledCursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 1, firstCursor, byteLimit: 1_000_000, NoEntryCap, cts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancelledPage.Count, Is.EqualTo(0), "a request cancelled before any row is processed must not report any data as served");
            Assert.That(cancelledCursor, Is.EqualTo(firstCursor), "a cancelled scan must report a resumable position, never a null 'fully drained' cursor that would silently drop the remaining addresses");
        }

        cancelledPage.Dispose();
    }

    [Test]
    public async Task GetChangesets_served_from_sidecar_not_readpath_columns()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryChangesetSidecarEnabled = true };
        ChangesetSidecarStore sidecar = new(_historyColumns.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            sidecar.RecordChunk(1, 0, [1, 2, 3], batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(config);

        List<ChangesetChunkEntry> chunks = [];
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(1, 1, 1_000_000, NoChunkCap, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.That(chunks, Is.Not.Empty, "a captured block with the sidecar enabled must be servable from GetChangesets");
    }

    [Test]
    public async Task GetChangesets_WhenSidecarDisabled_ReturnsNothingEvenWithCapturedHistory()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, TestItem.AddressA, block: 1, new Account(1, 100));
        HistoryColumnsWriter.MarkBlock(_historyColumns, 1, TestItem.KeccakA);
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true, HistoryChangesetSidecarEnabled = false });

        List<ChangesetChunkEntry> chunks = [];
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(1, 1, 1_000_000, NoChunkCap, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.That(chunks, Is.Empty, "GetChangesets must never fall through to the read-path history columns when the sidecar is off");
    }

    [Test]
    public async Task GetChangesets_RealMultiChunkSplit_IsLastChunkForBlockReflectsStorageNotScanCap()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryChangesetSidecarEnabled = true };
        ChangesetSidecarStore sidecar = new(_historyColumns.GetColumnDb(FlatHistoryColumns.ChangesetSidecar));

        List<ChangesetAccountEntry> entries = [];
        byte[] bigSlotValue = new byte[2000];
        for (int i = 0; i < 1000; i++)
        {
            Address address = new(Overwrite(TestItem.AddressA.Bytes.ToArray(), i));
            List<ChangesetSlotEntry> slots = [new ChangesetSlotEntry(new UInt256((ulong)i), bigSlotValue, ReadOnlyMemory<byte>.Empty)];
            entries.Add(new ChangesetAccountEntry(address, true, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty, slots));
        }

        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            sidecar.RecordChangeset(5, entries, batch.GetColumnBatch(FlatHistoryColumns.ChangesetSidecar));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 5);

        HistoryServer server = CreateServer(config);

        List<ChangesetChunkEntry> chunks = [];
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(5, 5, 1_000_000_000, NoChunkCap, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunks.Count, Is.GreaterThan(1), "precondition: 1000 large slot values must force EncodeChunked to split this one block across more than one real chunk");
            for (int i = 0; i < chunks.Count - 1; i++)
            {
                Assert.That(chunks[i].IsLastChunkForBlock, Is.False, $"chunk {i} of {chunks.Count} has a real successor chunk on disk and must not report completeness");
            }
            Assert.That(chunks[^1].IsLastChunkForBlock, Is.True, "the actual final chunk (confirmed absent from storage at index+1) must report completeness");
        }
    }

    private static byte[] Overwrite(byte[] source, int index)
    {
        byte[] copy = (byte[])source.Clone();
        copy[0] = (byte)(index % 256);
        copy[1] = (byte)(index / 256);
        return copy;
    }
}
