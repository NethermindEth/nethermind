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
using Nethermind.State.SnapServer;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryServerTests
{
    private static readonly Address[] Addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE];

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp() => _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

    [TearDown]
    public void TearDown() => _historyColumns.Dispose();

    private HistoryServer CreateServer(FlatDbConfig config) => new(_historyColumns, config);

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
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 1, cursor: null, byteLimit: 1, CancellationToken.None);

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
                ValueKeccak.Zero, ValueKeccak.MaxValue, height: 1, cursor, byteLimit: 1, CancellationToken.None);
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
        int pagesRemaining = Addresses.Length + 1;
        do
        {
            Assert.That(--pagesRemaining, Is.GreaterThan(0), "a block-0 row's suffix is all 0xFF - the exact case the cursor-skip fix protects - so a broken fix would hang here instead of failing");
            (IOwnedReadOnlyList<HistoryRangeEntry> page, byte[]? next) = server.GetHistoryRangeAtHeight(
                ValueKeccak.Zero, ValueKeccak.MaxValue, height: 10, cursor, byteLimit: 1, CancellationToken.None);
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
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 12, cursor: null, byteLimit: 1_000_000, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(1), "one key was written, so exactly one entry must resolve");
            Assert.That(entries[0].Block, Is.EqualTo(5UL), "height 12 sits between the two writes, so the block-5 version is the correct as-of answer");
            Assert.That(cursor, Is.Null, "a fully-drained scan must not hand back a continuation cursor");
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRangeAtHeight_WhenHeightAboveWatermark_ReturnsNothing()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, TestItem.AddressA, block: 5, new Account(5, 500));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 10);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 50, cursor: null, byteLimit: 1_000_000, CancellationToken.None);

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
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 10, cursor: null, byteLimit: 1_000_000, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(0), "a height below the retention floor may be answered from partially-pruned data, so it must be refused outright rather than served as if authoritative");
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
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(10, 10, 1_000_000, CancellationToken.None))
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
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(1, 1, 1_000_000, cts.Token))
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
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(50, 50, 1_000_000, CancellationToken.None))
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
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 1, cursor: null, byteLimit: 1, CancellationToken.None);
        Assert.That(firstCursor, Is.Not.Null, "precondition: the first capped page must hand back a resumable cursor");
        firstPage.Dispose();

        using CancellationTokenSource cts = new();
        cts.Cancel();
        (IOwnedReadOnlyList<HistoryRangeEntry> cancelledPage, byte[]? cancelledCursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 1, firstCursor, byteLimit: 1_000_000, cts.Token);

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
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(1, 1, 1_000_000, CancellationToken.None))
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
        await foreach (ChangesetChunkEntry chunk in server.GetChangesets(1, 1, 1_000_000, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.That(chunks, Is.Empty, "GetChangesets must never fall through to the read-path history columns when the sidecar is off");
    }
}
