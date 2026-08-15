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
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class HistoryServerTests
{
    private static readonly Address[] Addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE];
    private const int NoEntryCap = 1_000_000;
    private const int NoChunkCap = 1_000_000;

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _flatColumns = null!;

    [SetUp]
    public void SetUp()
    {
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _flatColumns = new SnapshotableMemColumnsDb<FlatDbColumns>();
    }

    [TearDown]
    public void TearDown()
    {
        _historyColumns.Dispose();
        _flatColumns.Dispose();
    }

    private IDb _codeDb = null!;

    private HistoryServer CreateServer(FlatDbConfig config) => CreateServer(config, new TestCaptureStatus());

    private HistoryServer CreateServer(FlatDbConfig config, TestCaptureStatus captureStatus)
    {
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        _codeDb = new SnapshotableMemDb();
        return new HistoryServer(_flatColumns, _historyColumns, _codeDb, config, availability, rowFormat, captureStatus, new HistoryScopeGate());
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
    public void GetHistoryRangeAtHeight_OnWindowedV3Database_MergesLiveFlatFallbackForKeysWithNoLaterHistoryRow()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 1000 };
        Account addressAPreValue = new(5, 500);
        Account addressALiveValue = new(999, 99900);
        Account addressBLiveValue = new(7, 700);
        Account addressCLiveValue = new(3, 300);

        HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.AddressA, block: 20, addressAPreValue);
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.AddressB, block: 5, account: null);
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        HistoryColumnsWriter.SetPersistedAccount(_flatColumns, TestItem.AddressA, addressALiveValue);
        HistoryColumnsWriter.SetPersistedAccount(_flatColumns, TestItem.AddressB, addressBLiveValue);
        HistoryColumnsWriter.SetPersistedAccount(_flatColumns, TestItem.AddressC, addressCLiveValue);

        HistoryServer server = CreateServer(config);

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 12, cursor: null, byteLimit: 1_000_000, NoEntryCap, CancellationToken.None);

        Dictionary<string, HistoryRangeEntry> byAddress = [];
        foreach (HistoryRangeEntry entry in entries) byAddress[Convert.ToHexString(entry.Key)] = entry;

        string addressAKey = Convert.ToHexString(AccountKeyOf(TestItem.AddressA));
        string addressBKey = Convert.ToHexString(AccountKeyOf(TestItem.AddressB));
        string addressCKey = Convert.ToHexString(AccountKeyOf(TestItem.AddressC));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(3), "all three addresses must resolve exactly once each - history matches and flat fallbacks together, with no duplication");
            Assert.That(cursor, Is.Null, "a fully-drained scan must not hand back a continuation cursor");

            Assert.That(byAddress[addressAKey].Block, Is.EqualTo(20UL), "AddressA has a real history row above the height and must resolve from it, not the flat fallback");
            Assert.That(byAddress[addressAKey].Value.ToArray(), Is.EqualTo(EncodedAccount(addressAPreValue)), "AddressA's history pre-value must win over its (different) live flat value");

            Assert.That(byAddress[addressBKey].Block, Is.EqualTo(12UL), "AddressB has no history row above the height, so the reported block is the queried height itself");
            Assert.That(byAddress[addressBKey].Value.ToArray(), Is.EqualTo(EncodedAccount(addressBLiveValue)), "AddressB must resolve to its live flat value - nothing changed it between its last captured change and the watermark");

            Assert.That(byAddress[addressCKey].Block, Is.EqualTo(12UL), "AddressC was never captured in history, so the reported block is the queried height itself");
            Assert.That(byAddress[addressCKey].Value.ToArray(), Is.EqualTo(EncodedAccount(addressCLiveValue)), "AddressC must resolve to its live flat value via the fallback, not be silently omitted");
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRangeAtHeight_AccountCreatedAboveHeightWithLiveValue_EmitsTheEmptyHistoryRowNotTheLiveValue()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 1000 };
        Account liveValueAfterCreation = new(1, 100);

        HistoryColumnsWriter.RecordAccountV3(_historyColumns, TestItem.AddressA, block: 20, account: null);
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 30);
        HistoryColumnsWriter.SetPersistedAccount(_flatColumns, TestItem.AddressA, liveValueAfterCreation);

        HistoryServer server = CreateServer(config);

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 12, cursor: null, byteLimit: 1_000_000, NoEntryCap, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(1), "the account has a real (empty) history answer at height 12 - the merge must not omit it or duplicate it with a flat entry");
            Assert.That(entries[0].Block, Is.EqualTo(20UL), "the reported block is the creation row that answered the query");
            Assert.That(entries[0].Value.Length, Is.EqualTo(0), "the account did not exist as of height 12 - its live (post-creation) flat value must never shadow that empty history answer");
            Assert.That(cursor, Is.Null);
        }

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRangeAtHeight_PagedWithATightByteLimit_SurfacesEveryFlatOnlyKeyExactlyOnceWithinTheCap()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 1000 };
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);
        foreach (Address address in Addresses)
        {
            HistoryColumnsWriter.SetPersistedAccount(_flatColumns, address, new Account(1, 100));
        }

        HistoryServer server = CreateServer(config);

        List<byte[]> seenKeys = [];
        byte[]? cursor = null;
        int pagesRemaining = Addresses.Length + 1;
        do
        {
            Assert.That(--pagesRemaining, Is.GreaterThan(0), "a broken cursor must not be able to hang this loop instead of failing it");
            (IOwnedReadOnlyList<HistoryRangeEntry> page, byte[]? next) = server.GetHistoryRangeAtHeight(
                ValueKeccak.Zero, ValueKeccak.MaxValue, height: 20, cursor, byteLimit: 1, NoEntryCap, CancellationToken.None);
            Assert.That(page.Count, Is.LessThanOrEqualTo(1), "a 1-byte budget must cap every page at exactly one entry");
            foreach (HistoryRangeEntry entry in page) seenKeys.Add(entry.Key);
            page.Dispose();
            cursor = next;
        } while (cursor is not null);

        Assert.That(seenKeys.Select(Convert.ToHexString).Distinct().Count(), Is.EqualTo(Addresses.Length),
            "every flat-only address must surface exactly once across the paged responses");
    }

    [Test]
    public void GetHistoryRangeAtHeight_WhenCaptureIsUnhealthy_OmitsTheLiveFlatFallbackInsteadOfServingAStaleValue()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 1000 };
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);
        HistoryColumnsWriter.SetPersistedAccount(_flatColumns, TestItem.AddressA, new Account(1, 100));

        TestCaptureStatus captureStatus = new() { CaptureHealthy = false };
        HistoryServer server = CreateServer(config, captureStatus);

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 12, cursor: null, byteLimit: 1_000_000, NoEntryCap, CancellationToken.None);

        Assert.That(entries.Count, Is.EqualTo(0),
            "the live-flat fallback is only sound while capture keeps the flat column pinned to the watermark - once capture is disabled the flat column may have run ahead, so the fallback must be omitted rather than risk serving a wrong value");
        Assert.That(cursor, Is.Null);

        entries.Dispose();
    }

    [Test]
    public void GetHistoryRangeAtHeight_OnUnwindowedV2Database_NeverMergesLiveFlatFallback()
    {
        HistoryColumnsWriter.RecordAccount(_historyColumns, TestItem.AddressA, block: 5, new Account(5, 500));
        HistoryColumnsWriter.SetWatermark(_historyColumns, 20);
        HistoryColumnsWriter.SetPersistedAccount(_flatColumns, TestItem.AddressB, new Account(3, 300));

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRangeEntry> entries, byte[]? cursor) = server.GetHistoryRangeAtHeight(
            ValueKeccak.Zero, ValueKeccak.MaxValue, height: 12, cursor: null, byteLimit: 1_000_000, NoEntryCap, CancellationToken.None);

        Assert.That(entries.Count, Is.EqualTo(1), "v2 has no live-flat fallback rule at all - AddressA resolves from its own complete history, AddressB's flat-only presence must never leak in");

        entries.Dispose();
    }

    private static byte[] AccountKeyOf(Address address)
    {
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.AccountKeyLength];
        return BaseFlatPersistence.EncodeAccountKeyHashed(buffer, address.ToAccountPath).ToArray();
    }

    private static byte[] EncodedAccount(Account account)
    {
        using ArrayPoolSpan<byte> rlp = AccountDecoder.Slim.EncodeToArrayPoolSpan(account);
        return ((ReadOnlySpan<byte>)rlp).ToArray();
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
            HistoryRowColumn.AccountHistory, [0x00], Enumerable.Repeat((byte)0xFF, 64).ToArray(), null, 1_000_000, NoEntryCap, CancellationToken.None);

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
    public void GetHistoryRows_ScanCancelledBeforeGatheringAnything_RefusesRatherThanRepeatingTheCursor()
    {
        for (int i = 0; i < Addresses.Length; i++)
        {
            HistoryColumnsWriter.RecordAccount(_historyColumns, Addresses[i], block: 1, new Account((ulong)i + 1, (ulong)(i + 1) * 100));
        }
        HistoryColumnsWriter.SetWatermark(_historyColumns, 1);

        HistoryServer server = CreateServer(new FlatDbConfig { HistoryEnabled = true });

        (IOwnedReadOnlyList<HistoryRowEntry> firstPage, byte[]? resumeFrom, bool _) = server.GetHistoryRows(
            HistoryRowColumn.AccountHistory, [0x00], Enumerable.Repeat((byte)0xFF, 64).ToArray(), null, 1_000_000, 1, CancellationToken.None);
        Assert.That(firstPage.Count, Is.EqualTo(1), "precondition: the entry cap stops the scan after one row and hands back a cursor");
        firstPage.Dispose();

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        (IOwnedReadOnlyList<HistoryRowEntry> entries, byte[]? cursor, bool refused) = server.GetHistoryRows(
            HistoryRowColumn.AccountHistory, [0x00], Enumerable.Repeat((byte)0xFF, 64).ToArray(), resumeFrom, 1_000_000, NoEntryCap, cancelled.Token);

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

        byte[] maxKey = Enumerable.Repeat((byte)0xFF, 64).ToArray();

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
            HistoryRowColumn.AccountHistory, [0x00], Enumerable.Repeat((byte)0xFF, 64).ToArray(), null, 1_000_000, NoEntryCap, CancellationToken.None);

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
            HistoryRowColumn.AccountHistory, [0x00], Enumerable.Repeat((byte)0xFF, 64).ToArray(), null, 1_000_000, NoEntryCap, CancellationToken.None);

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
        byte[] maxKey = Enumerable.Repeat((byte)0xFF, 64).ToArray();

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
            HistoryRowColumn.AccountHistory, [0x00], Enumerable.Repeat((byte)0xFF, 64).ToArray(), null, 1_000_000, NoEntryCap, CancellationToken.None);

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
            HistoryRowColumn.AccountHistory, [0x00], Enumerable.Repeat((byte)0xFF, 64).ToArray(), null, 1_000_000, NoEntryCap, cts.Token);

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
