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

    private static readonly byte[] KeyPastAnyAccountHistoryRow = Enumerable.Repeat((byte)0xFF, HistoryKeyLayout.AccountKeyLength + sizeof(ulong) + 1).ToArray();

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp() => _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

    [TearDown]
    public void TearDown() => _historyColumns.Dispose();

    private IDb _codeDb = null!;

    private HistoryServer CreateServer(FlatDbConfig config)
    {
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        _codeDb = new SnapshotableMemDb();
        return new HistoryServer(_historyColumns, _codeDb, config, availability, rowFormat, new HistoryScopeGate());
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
    public void ServedScopes_ForAPublishedSliceScope_SpansEveryAccountPathSharingItsScopeKey()
    {
        HistoryColumnsWriter.SetWatermark(_historyColumns, 100);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 20);
        byte[] scopeKey = TestItem.AddressA.ToAccountPath.Bytes[..HistoryKeyLayout.ScopeKeyLength].ToArray();
        new HistoryAvailability(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks)).PublishScope(scopeKey, floor: 50);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        IReadOnlyList<HistoryServingScope> scopes = server.ServedScopes;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scopes.Count, Is.EqualTo(2), "the general window plus the one published slice scope");
            HistoryServingScope slice = scopes[1];
            Assert.That(slice.KeyRangeStart.Bytes[..HistoryKeyLayout.ScopeKeyLength].ToArray(), Is.EqualTo(scopeKey));
            Assert.That(slice.KeyRangeStart.Bytes[HistoryKeyLayout.ScopeKeyLength..].ToArray(), Is.EqualTo(new byte[Hash256.Size - HistoryKeyLayout.ScopeKeyLength]),
                "the start bound must not exclude any account path whose tail is nonzero");
            Assert.That(slice.KeyRangeEnd.Bytes[..HistoryKeyLayout.ScopeKeyLength].ToArray(), Is.EqualTo(scopeKey));
            foreach (byte tailByte in slice.KeyRangeEnd.Bytes[HistoryKeyLayout.ScopeKeyLength..].ToArray())
            {
                Assert.That(tailByte, Is.EqualTo((byte)0xFF), "the end bound must not exclude any account path whose tail is nonzero");
            }
            Assert.That(slice.KeyRangeEnd, Is.GreaterThan(slice.KeyRangeStart),
                "a degenerate start-equals-end range would advertise a slice no peer's key-range scan could ever match");
            Assert.That(slice.FloorBlock, Is.EqualTo(50UL));
        }
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

    [Test]
    public void GetHistoryRows_UnwindowedNode_StreamsRawAccountHistoryRowsInAscendingKeyOrder()
    {
        for (int i = 0; i < Addresses.Length; i++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, Addresses[i], block: 1, new Account((ulong)i + 1, (ulong)(i + 1) * 100));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRowEntry> entries, byte[]? cursor, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.AccountHistory, [0x00], KeyPastAnyAccountHistoryRow, null, 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.False, "an unwindowed (full) archive must serve a full clone request");
            Assert.That(entries.Count, Is.EqualTo(Addresses.Length), "every raw on-disk row in range must stream out, unmerged with anything else");
            Assert.That(cursor, Is.Null, "a fully-drained scan must not hand back a continuation cursor");
            for (int i = 1; i < entries.Count; i++)
            {
                int comparison = string.CompareOrdinal(Convert.ToHexString(entries[i - 1].Key), Convert.ToHexString(entries[i].Key));
                Assert.That(comparison, Is.LessThan(0),
                    "rows must stream in ascending on-disk key order, matching the shard/bulk-write import pipeline's expectations");
            }
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRows_Code_StreamsContractCodeInsideTheImportersShardBounds()
    {
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);
        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        // Code keys are 32-byte hashes; seed one inside the first importer shard, which covers first bytes 0x00-0x0F.
        byte[] codeHash = new byte[32];
        codeHash[0] = 0x05;
        _codeDb.PutSpan(codeHash, [0x60, 0x00]);

        // The bounds the importer sends for shard 0: a one-byte start and a MaxRowKeyBytes-long exclusive end.
        byte[] shardStart = [0x00];
        byte[] shardEnd = new byte[IHistoryServer.MaxRowKeyBytes];
        shardEnd[0] = 0x0F;
        shardEnd.AsSpan(1).Fill(0xFF);

        (IOwnedReadOnlyList<HistoryRowEntry> entries, byte[]? cursor, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.Code, shardStart, shardEnd, null, 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.False, "the code column is not versioned, so it must be servable");
            Assert.That(entries.Count, Is.EqualTo(1), "a code row inside the shard's key range must stream out");
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRows_ScanCancelledBeforeGatheringAnything_RefusesRatherThanRepeatingTheCursor()
    {
        for (int i = 0; i < Addresses.Length; i++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, Addresses[i], block: 1, new Account((ulong)i + 1, (ulong)(i + 1) * 100));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRowEntry> firstPage, byte[]? resumeFrom, bool _) = server.GetHistoryRows(
            HistoryRowColumn.AccountHistory, [0x00], KeyPastAnyAccountHistoryRow, null, 1_000_000, 1, CancellationToken.None);
        Assert.That(firstPage.Count, Is.EqualTo(1), "precondition: the entry cap stops the scan after one row and hands back a cursor");
        firstPage.Dispose();

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        (IOwnedReadOnlyList<HistoryRowEntry> entries, byte[]? cursor, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.AccountHistory, [0x00], KeyPastAnyAccountHistoryRow, resumeFrom, 1_000_000, NoEntryCap, cancelled.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.True,
                "with nothing gathered there is no cursor that moves the requester on, and echoing the one it sent would stall its scan");
            Assert.That(cursor, Is.Null);
            Assert.That(entries.Count, Is.Zero);
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRows_WindowedNode_RefusesVersionedColumnsIncludingAvailableBlocks()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.AddressA, block: 5, new Account(5, 500));
        HistoryColumnsWriter.MarkBlockV3(_historyColumns, 5, ValueKeccak.Compute("root"u8));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 5);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 10 });

        byte[] maxKey = KeyPastAnyAccountHistoryRow;

        using (Assert.EnterMultipleScope())
        {
            AssertRefused(server.GetHistoryRows(HistoryRowColumn.AccountHistory, [0x00], maxKey, null, 1_000_000, NoEntryCap, CancellationToken.None));
            AssertRefused(server.GetHistoryRows(HistoryRowColumn.StorageHistory, [0x00], maxKey, null, 1_000_000, NoEntryCap, CancellationToken.None));
            AssertRefused(server.GetHistoryRows(HistoryRowColumn.StorageClears, [0x00], maxKey, null, 1_000_000, NoEntryCap, CancellationToken.None));
            AssertRefused(server.GetHistoryRows(HistoryRowColumn.AvailableBlocks, [0x00], maxKey, null, 1_000_000, NoEntryCap, CancellationToken.None));
        }

        return;

        static void AssertRefused((IOwnedReadOnlyList<HistoryRowEntry> Entries, byte[]? Cursor, bool Refused) result)
        {
            Assert.That(result.Refused, Is.True, "a windowed source must refuse a full-clone request for a versioned column, including AvailableBlocks, which the pruner also deletes below the floor");
            result.Entries.Dispose();
        }
    }

    [Test]
    public void GetHistoryRows_WindowedNode_StillServesCode()
    {
        HistoryColumnsWriter.MarkBlockV3(_historyColumns, 5, ValueKeccak.Compute("root"u8));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 5);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 10 });
        _codeDb.Set(new byte[] { 1, 2, 3 }, new byte[] { 0xAB });

        byte[] maxKey = Enumerable.Repeat((byte)0xFF, 32).ToArray();

        (IOwnedReadOnlyList<HistoryRowEntry> code, _, bool codeRefused) = server.GetHistoryRows(
            HistoryRowColumn.Code, [0x00], maxKey, null, 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeRefused, Is.False, "code is content-addressed and never pruned, so it must remain servable even when windowed");
            Assert.That(code.Count, Is.EqualTo(1));
        }

        code.Dispose();
    }

    [Test]
    public void IsWindowed_RetentionConfiguredAloneWithNoFloorPublished_Refuses()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.AddressA, block: 5, new Account(5, 500));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 5);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 10 });

        (IOwnedReadOnlyList<HistoryRowEntry> entries, _, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.AccountHistory, [0x00], KeyPastAnyAccountHistoryRow, null, 1_000_000, NoEntryCap, CancellationToken.None);

        Assert.That(refused, Is.True, "configured retention alone, even with no floor ever published yet, must count as windowed");
        entries.Dispose();
    }

    [Test]
    public void IsWindowed_FloorPublishedHistoricallyWithNoCurrentRetentionConfigured_Refuses()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.AddressA, block: 5, new Account(5, 500));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 5);
        HistoryColumnsWriter.SetGlobalFloor(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 0 });

        (IOwnedReadOnlyList<HistoryRowEntry> entries, _, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.AccountHistory, [0x00], KeyPastAnyAccountHistoryRow, null, 1_000_000, NoEntryCap, CancellationToken.None);

        Assert.That(refused, Is.True, "a floor published historically must be refused even if retention is unset in the current config - rows below it are already gone from disk");
        entries.Dispose();
    }

    [Test]
    public void GetHistoryRows_response_never_exceeds_soft_cap_and_cursor_resumes_correctly()
    {
        for (int i = 0; i < Addresses.Length; i++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, Addresses[i], block: 1, new Account((ulong)i + 1, (ulong)(i + 1) * 100));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });
        byte[] maxKey = KeyPastAnyAccountHistoryRow;

        List<byte[]> collectedKeys = [];
        byte[]? cursor = null;
        int pagesRemaining = Addresses.Length + 1;
        do
        {
            Assert.That(--pagesRemaining, Is.GreaterThan(0), "the cursor must eventually drain, not loop forever");
            (IOwnedReadOnlyList<HistoryRowEntry> page, byte[]? next, bool refused) = server.GetHistoryRows(
                HistoryRowColumn.AccountHistory, [0x00], maxKey, cursor, byteLimit: 1, NoEntryCap, CancellationToken.None);
            Assert.That(refused, Is.False);
            foreach (HistoryRowEntry entry in page) collectedKeys.Add(entry.Key);
            page.Dispose();
            cursor = next;
        } while (cursor is not null);

        Assert.That(collectedKeys.Count, Is.EqualTo(Addresses.Length),
            "resuming with the returned cursor repeatedly must surface every row exactly once under a tight byte cap - a raw count, not deduplicated, so a resume bug that repeats a key cannot hide behind Distinct()");
    }

    [Test]
    public void GetHistoryRows_NullCursor_ScansFromStartKey()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, TestItem.AddressA, block: 1, new Account(1, 100));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRowEntry> entries, byte[]? cursor, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.AccountHistory, [0x00], KeyPastAnyAccountHistoryRow, null, 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.False);
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(cursor, Is.Null, "a single-row scan that fits entirely in one page must not hand back a cursor");
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRows_Cancelled_ReturnsRefusedTrue_NeverAnEmptyResultWithNoCursor()
    {
        for (int i = 0; i < Addresses.Length; i++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, Addresses[i], block: 1, new Account((ulong)i + 1, (ulong)(i + 1) * 100));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        using CancellationTokenSource cts = new();
        cts.Cancel();

        (IOwnedReadOnlyList<HistoryRowEntry> entries, byte[]? cursor, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.AccountHistory, [0x00], KeyPastAnyAccountHistoryRow, null, 1_000_000, NoEntryCap, cts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.True, "a cancelled scan must report Refused=true, never look like a clean, complete, empty result");
            Assert.That(entries.Count, Is.EqualTo(0));
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRows_AvailableBlocks_FiltersOutReservedKeysStructurally()
    {
        HistoryColumnsWriter.MarkBlock(_historyColumns, 5, ValueKeccak.Compute("root"u8));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 5);

        IDb availableBlocks = _historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
        availableBlocks.Set("history:some-reserved-key"u8, new byte[] { 1 });

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRowEntry> entries, _, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.AvailableBlocks, new byte[8], Enumerable.Repeat((byte)0xFF, 32).ToArray(), null, 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.False);
            Assert.That(entries.Count, Is.EqualTo(1), "only the real 8-byte per-block marker key must be served; reserved (non-8-byte) keys must never reach the wire as if they were block markers");
            Assert.That(entries[0].Key.Length, Is.EqualTo(8));
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRows_WhenHistoryDisabled_Refuses()
    {
        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = false });

        (IOwnedReadOnlyList<HistoryRowEntry> entries, byte[]? cursor, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.Code, [0x00], [0xFF], null, 1_000_000, NoEntryCap, CancellationToken.None);

        Assert.That(refused, Is.True);
        entries.Dispose();
    }
}
