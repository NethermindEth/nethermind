// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ArchiveCloneImporterTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private IDb _codeDb = null!;
    private IDb _metadataDb = null!;
    private HistoryWindowPruner _pruner = null!;
    private HistoryWriter _writer = null!;
    private HistoryAvailability _availability = null!;
    private HistoryRowFormat _rowFormat = null!;
    private FlatDbConfig _config = null!;
    private FakeHeaderSource _headers = null!;

    [SetUp]
    public void SetUp()
    {
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _codeDb = new SnapshotableMemDb();
        _metadataDb = new SnapshotableMemDb();
        _headers = new FakeHeaderSource();
        _config = new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 0, HistoryImportShardCount = 4, HistoryImportShardBufferBudgetEntries = 6 };
        (_availability, _rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, _config);
        _writer = new HistoryWriter(_db, _historyColumns, _config, _availability, _rowFormat, LimboLogs.Instance);
        _pruner = new HistoryWindowPruner(
            _writer, _historyColumns, _config, NullBackfillInterlock.Instance, new HistoryScopeGate(), _availability, _rowFormat, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _pruner.Dispose();
        _historyColumns.Dispose();
        _db.Dispose();
        _codeDb.Dispose();
        _metadataDb.Dispose();
    }

    private ArchiveCloneImporter CreateImporter(IArchiveCloneSource source) =>
        new(source, _historyColumns, _codeDb, _metadataDb, _config, _pruner, _availability, _rowFormat,
            new ArchiveCloneVerifier(_availability, _headers, LimboLogs.Instance), LimboLogs.Instance);

    /// Seeds a block's state root on the source and on this node's headers at once: a clone only publishes when
    /// the two agree, so a test that wants a successful clone has to set up both sides.
    private void SeedAgreedBlock(FakeCloneSource source, ulong block, ValueHash256 root)
    {
        byte[] key = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(key, block);
        source.Seed(HistoryRowColumn.AvailableBlocks, (key, root.BytesAsSpan.ToArray()));
        _headers.Roots[block] = root;
    }

    [Test]
    public void CloneAsync_WhenSourceCannotServeFullClone_RefusesWithoutTouchingAnyColumn()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { SupportsFullClone = false };
        source.Seed(HistoryRowColumn.AccountHistory, ([1, 2, 3], [9, 9]));

        ArchiveCloneImporter importer = CreateImporter(source);

        Assert.That(async () => await importer.CloneAsync(CancellationToken.None), Throws.InstanceOf<InvalidConfigurationException>(),
            "a windowed/partial source must be refused outright rather than silently imported as if it were a complete history");
        Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory).Get(new byte[] { 1, 2, 3 }), Is.Null, "no row may land locally before the refusal check");
    }

    [Test]
    public void CloneAsync_WhenSourceWatermarkDoesNotRiseAboveTheLocalFloor_RefusesWithoutTouchingAnyColumn()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 0 };
        source.Seed(HistoryRowColumn.AccountHistory, ([1, 2, 3], [9, 9]));

        ArchiveCloneImporter importer = CreateImporter(source);

        Assert.That(async () => await importer.CloneAsync(CancellationToken.None), Throws.InstanceOf<InvalidConfigurationException>(),
            "an empty range is the one input the sampled-root check cannot inspect, so it must be refused up front instead of importing rows no sample can ever cover");
        Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory).Get(new byte[] { 1, 2, 3 }), Is.Null, "no row may land locally before the refusal check");
    }

    [Test]
    public void CloneAsync_WhenSourceFormatDoesNotMatchLocallyResolvedFormat_Refuses()
    {
        FakeCloneSource source = new((byte)(_rowFormat.FormatVersion + 1));

        ArchiveCloneImporter importer = CreateImporter(source);

        Assert.That(async () => await importer.CloneAsync(CancellationToken.None), Throws.InstanceOf<InvalidConfigurationException>(),
            "no transcoding is supported; a format mismatch must refuse the same way SupportsFullClone=false does");
    }

    [Test]
    public async Task CloneAsync_CopiesEveryRowByteIdenticalAcrossAllShardsAndAllFiveColumns()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 5 };
        for (int i = 0; i < 40; i++)
        {
            byte[] key = [(byte)(i * 6), 0, 0];
            source.Seed(HistoryRowColumn.AccountHistory, (key, [(byte)i]));
            source.Seed(HistoryRowColumn.StorageHistory, (key, [(byte)(i + 1)]));
            source.Seed(HistoryRowColumn.StorageClears, (key, [(byte)(i + 2)]));
            byte[] code = [(byte)(i + 3)];
            source.Seed(HistoryRowColumn.Code, (ValueKeccak.Compute(code).BytesAsSpan.ToArray(), code));
        }
        SeedAgreedBlock(source, 5, ValueKeccak.Compute("root"u8));

        ArchiveCloneImporter importer = CreateImporter(source);
        await importer.CloneAsync(CancellationToken.None);

        IDb accountHistory = _historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory);
        IDb storageHistory = _historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory);
        IDb storageClears = _historyColumns.GetColumnDb(FlatHistoryColumns.StorageClears);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < 40; i++)
            {
                byte[] key = [(byte)(i * 6), 0, 0];
                Assert.That(accountHistory.Get(key), Is.EqualTo(new byte[] { (byte)i }), $"AccountHistory row {i} spread across the full byte range must land in whichever shard covers it");
                Assert.That(storageHistory.Get(key), Is.EqualTo(new byte[] { (byte)(i + 1) }));
                Assert.That(storageClears.Get(key), Is.EqualTo(new byte[] { (byte)(i + 2) }));
                byte[] code = [(byte)(i + 3)];
                Assert.That(_codeDb.Get(ValueKeccak.Compute(code).BytesAsSpan), Is.EqualTo(code), "code is keyed by its own hash");
            }

            Assert.That(_availability.TryGetWatermark(out ulong watermark), Is.True, "a fully completed clone must publish the source's watermark");
            Assert.That(watermark, Is.EqualTo(5UL));
        }
    }

    [Test]
    public async Task CloneAsync_AvailableBlocks_FiltersOutReservedKeysOnImport()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 5 };
        SeedAgreedBlock(source, 5, ValueKeccak.Compute("root"u8));
        source.Seed(HistoryRowColumn.AvailableBlocks, ("history:some-reserved-key"u8.ToArray(), [1]));

        ArchiveCloneImporter importer = CreateImporter(source);
        await importer.CloneAsync(CancellationToken.None);

        IDb availableBlocks = _historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
        Assert.That(availableBlocks.Get("history:some-reserved-key"u8), Is.Null,
            "a reserved (non-8-byte) key received from the source must never be written as if it were a real per-block marker");
    }

    [Test]
    public async Task CloneAsync_InterruptedBeforeAvailableBlocksCompletes_LeavesAFreshReaderSeeingNothingCovered()
    {
        FakeCloneSource throwingSource = new(_rowFormat.FormatVersion) { Watermark = 5 };
        throwingSource.Seed(HistoryRowColumn.AccountHistory, ([1], [1]));
        throwingSource.ThrowOnColumn = HistoryRowColumn.AvailableBlocks;

        ArchiveCloneImporter interrupted = CreateImporter(throwingSource);
        Assert.That(async () => await interrupted.CloneAsync(CancellationToken.None), Throws.InstanceOf<InvalidOperationException>(),
            "precondition: the source must actually interrupt the clone before AvailableBlocks completes");

        HistoryAvailability freshReader = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        Assert.That(freshReader.TryGetWatermark(out _), Is.False,
            "no watermark may be observable until every column, AvailableBlocks last, has landed - a partial clone must fail closed, never look partially covered");
    }

    [Test]
    public async Task CloneAsync_InterruptedMidShard_ResumesFromAfterTheLastDurablyWrittenKey()
    {
        FlatDbConfig singleShardConfig = new() { HistoryEnabled = true, HistoryImportShardCount = 1, HistoryImportShardBufferBudgetEntries = 6 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, singleShardConfig);

        List<(byte[] Key, byte[] Value)> rows = [];
        for (byte i = 0; i < 12; i++) rows.Add(([i], [i]));

        // Call 1 is the empty Code column, so the throw lands on the third AccountHistory page.
        FakeCloneSource throwingSource = new(rowFormat.FormatVersion) { PageSize = 3, ThrowOnCallNumber = 4, Watermark = 5 };
        SeedAgreedBlock(throwingSource, 5, ValueKeccak.Compute("root"u8));
        foreach ((byte[] key, byte[] value) in rows) throwingSource.Seed(HistoryRowColumn.AccountHistory, (key, value));

        ArchiveCloneImporter firstAttempt = new(throwingSource, _historyColumns, _codeDb, _metadataDb, singleShardConfig, _pruner, availability, rowFormat,
            new ArchiveCloneVerifier(availability, _headers, LimboLogs.Instance), LimboLogs.Instance);
        Assert.That(async () => await firstAttempt.CloneAsync(CancellationToken.None), Throws.InstanceOf<InvalidOperationException>(),
            "precondition: the fake source must actually interrupt the clone partway through the Code column");

        FakeCloneSource resumedSource = new(rowFormat.FormatVersion) { PageSize = 3, Watermark = 5 };
        SeedAgreedBlock(resumedSource, 5, ValueKeccak.Compute("root"u8));
        foreach ((byte[] key, byte[] value) in rows) resumedSource.Seed(HistoryRowColumn.AccountHistory, (key, value));

        ArchiveCloneImporter resumedAttempt = new(resumedSource, _historyColumns, _codeDb, _metadataDb, singleShardConfig, _pruner, availability, rowFormat,
            new ArchiveCloneVerifier(availability, _headers, LimboLogs.Instance), LimboLogs.Instance);
        await resumedAttempt.CloneAsync(CancellationToken.None);

        (HistoryRowColumn Column, byte[] StartKey) firstResumedCall = resumedSource.Calls.First(c => c.Column == HistoryRowColumn.AccountHistory);
        Assert.That(Bytes.BytesComparer.Compare(firstResumedCall.StartKey, new byte[] { 5 }), Is.GreaterThan(0),
            "the resumed run must start strictly after the last durably-committed row (index 5, the end of the first 6-row batch) - a deleted resume mechanism would start again from the shard's own beginning");

        IDb accountHistory = _historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory);
        foreach ((byte[] key, byte[] value) in rows)
        {
            Assert.That(accountHistory.Get(key), Is.EqualTo(value), $"row {Convert.ToHexString(key)} must be present and correct after resuming from the interrupted attempt");
        }
    }

    [Test]
    public async Task CloneAsync_RerunAfterCompletion_SkipsEveryColumnAndRepublishesTheOriginalWatermark()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 5 };
        SeedAgreedBlock(source, 5, ValueKeccak.Compute("root"u8));
        await CreateImporter(source).CloneAsync(CancellationToken.None);

        FakeCloneSource newer = new(_rowFormat.FormatVersion) { Watermark = 99 };
        await CreateImporter(newer).CloneAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newer.Calls, Is.Empty, "every column carries a done marker after a completed clone, so a re-run must not fetch a single page");
            Assert.That(_availability.TryGetWatermark(out ulong watermark), Is.True);
            Assert.That(watermark, Is.EqualTo(5UL), "a re-run against a source that moved forward must republish the stored watermark of the original run - the rows on disk cover nothing newer");
        }
    }

    [Test]
    public async Task ResetForNewTarget_MakesTheNextCloneRestreamAgainstTheSourcesCurrentWatermark()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 5 };
        SeedAgreedBlock(source, 5, ValueKeccak.Compute("root"u8));
        await CreateImporter(source).CloneAsync(CancellationToken.None);

        FakeCloneSource newer = new(_rowFormat.FormatVersion) { Watermark = 99 };
        newer.Seed(HistoryRowColumn.AccountHistory, ([1, 2, 3], [7]));
        SeedAgreedBlock(newer, 99, ValueKeccak.Compute("root99"u8));
        ArchiveCloneImporter second = CreateImporter(newer);
        second.ResetForNewTarget();
        ulong republished = await second.CloneAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newer.Calls, Is.Not.Empty, "after a reset the next clone must stream from the source again");
            Assert.That(republished, Is.EqualTo(99UL));
            Assert.That(_availability.TryGetWatermark(out ulong watermark), Is.True);
            Assert.That(watermark, Is.EqualTo(99UL), "the new pass publishes the fresh source watermark the re-streamed rows actually cover");
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory).Get([1, 2, 3]), Is.EqualTo(new byte[] { 7 }));
        }
    }

    [Test]
    public async Task CloneAsync_PageTimeout_IsRetriedAndSucceedsWhenTheSourceRecovers()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 5, TimeoutFirstNCalls = 2 };
        source.Seed(HistoryRowColumn.AccountHistory, ([1, 2, 3], [9, 9]));
        SeedAgreedBlock(source, 5, ValueKeccak.Compute("root"u8));

        await CreateImporter(source).CloneAsync(CancellationToken.None);

        Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory).Get(new byte[] { 1, 2, 3 }), Is.EqualTo(new byte[] { 9, 9 }),
            "a page timeout (a paused or briefly overloaded source) must be retried at page level, not tear the whole stream down");
    }

    [Test]
    public async Task CloneAsync_RefusedPage_IsRetriedAndSucceedsWhenTheSourceRecovers()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 5, RefuseFirstNCalls = 2 };
        source.Seed(HistoryRowColumn.AccountHistory, ([1, 2, 3], [9, 9]));
        SeedAgreedBlock(source, 5, ValueKeccak.Compute("root"u8));

        await CreateImporter(source).CloneAsync(CancellationToken.None);

        Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory).Get(new byte[] { 1, 2, 3 }), Is.EqualTo(new byte[] { 9, 9 }),
            "a transiently refusing source (server-side deadline, cancellation) must be retried, not treated as fatal");
    }

    [Test]
    public async Task CloneAsync_RefusalStreakLongerThanTheTimeoutBudget_IsWaitedOut()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 5, RefuseFirstNCalls = 6 };
        source.Seed(HistoryRowColumn.AccountHistory, ([1, 2, 3], [9, 9]));
        SeedAgreedBlock(source, 5, ValueKeccak.Compute("root"u8));

        await CreateImporter(source).CloneAsync(CancellationToken.None);

        Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory).Get(new byte[] { 1, 2, 3 }), Is.EqualTo(new byte[] { 9, 9 }),
            "refusal is explicit backpressure from a live source; a persist window longer than the timeout budget must be waited out, not treated as fatal");
    }

    [Test]
    public void CloneAsync_WhenImportedRootsDisagreeWithOwnHeaders_RefusesToPublish()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 5 };
        byte[] blockKey = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(blockKey, 5UL);
        source.Seed(HistoryRowColumn.AvailableBlocks, (blockKey, ValueKeccak.Compute("forged"u8).BytesAsSpan.ToArray()));
        _headers.Roots[5] = ValueKeccak.Compute("real"u8);

        ArchiveCloneImporter importer = CreateImporter(source);

        Assert.That(async () => await importer.CloneAsync(CancellationToken.None), Throws.InstanceOf<InvalidOperationException>(),
            "the source picked the rows but not this node's headers, so a state root that disagrees with them means the import cannot be trusted");
        Assert.That(_availability.TryGetWatermark(out _), Is.False,
            "an unverified range must not be published: this node would answer historical queries from it and serve it to the next consumer");
    }

    [Test]
    public async Task VerifyAndBan_HealthyClone_BansNobody()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 20 };
        Dictionary<ulong, Account> accounts = [];
        for (ulong block = 1; block <= 20; block++)
        {
            SeedAgreedBlock(source, block, ValueKeccak.Compute(BitConverter.GetBytes(block)));
        }

        ArchiveCloneImporter importer = CreateImporter(source);
        await importer.CloneAsync(CancellationToken.None);

        FakeHeaderSource headers = new();
        for (ulong block = 1; block <= 20; block++)
        {
            headers.Roots[block] = ValueKeccak.Compute(BitConverter.GetBytes(block));
        }

        ArchiveCloneVerifier verifier = new(_availability, headers, LimboLogs.Instance);
        FakePeerSink peerSink = new();

        ArchiveCloneVerdict verdict = importer.VerifyAndBan(verifier, sampleCount: 8, peerSink);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.True);
            Assert.That(peerSink.Banned, Is.Empty, "a healthy, fully-agreeing clone must never ban the source it just verified");
        }
    }

    [Test]
    public async Task VerifyAndBan_MarkerMismatch_BansTheSource()
    {
        FakeCloneSource source = new(_rowFormat.FormatVersion) { Watermark = 5 };
        SeedAgreedBlock(source, 5, ValueKeccak.Compute("root"u8));

        ArchiveCloneImporter importer = CreateImporter(source);
        await importer.CloneAsync(CancellationToken.None);

        // A later, deeper pass reads headers this node did not have at clone time and finds them disagreeing.
        FakeHeaderSource headers = new();
        headers.Roots[5] = ValueKeccak.Compute("real"u8);

        ArchiveCloneVerifier verifier = new(_availability, headers, LimboLogs.Instance);
        FakePeerSink peerSink = new();

        ArchiveCloneVerdict verdict = importer.VerifyAndBan(verifier, sampleCount: 1, peerSink);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Verified, Is.False);
            Assert.That(peerSink.Banned, Contains.Item(source), "a marker mismatch against the real header must ban the clone source that served it");
        }
    }

    private sealed class FakeCloneSource(byte rowFormatVersion) : IArchiveCloneSource
    {
        private readonly Dictionary<HistoryRowColumn, List<(byte[] Key, byte[] Value)>> _rows = [];
        private readonly Lock _lock = new();
        private int _callCount;

        public List<(HistoryRowColumn Column, byte[] StartKey)> Calls { get; } = [];

        public bool SupportsFullClone { get; set; } = true;

        public byte RowFormatVersion => rowFormatVersion;

        public ulong Watermark { get; set; }

        public int PageSize { get; set; } = int.MaxValue;

        public int RefuseFirstNCalls { get; set; }

        public int TimeoutFirstNCalls { get; set; }

        public int? ThrowOnCallNumber { get; set; }

        public HistoryRowColumn? ThrowOnColumn { get; set; }

        public void Seed(HistoryRowColumn column, (byte[] Key, byte[] Value) row)
        {
            if (!_rows.TryGetValue(column, out List<(byte[] Key, byte[] Value)>? list))
            {
                list = [];
                _rows[column] = list;
            }
            list.Add(row);
        }

        public Task<ArchiveCloneRowPage> GetHistoryRowsAsync(HistoryRowColumn column, byte[] startKey, byte[] endKey, byte[]? cursor, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Calls.Add((column, startKey));
                _callCount++;

                if (ThrowOnCallNumber == _callCount || ThrowOnColumn == column)
                {
                    throw new InvalidOperationException("simulated crash mid-clone");
                }

                if (_callCount <= TimeoutFirstNCalls)
                {
                    throw new TimeoutException("simulated paused source");
                }

                if (_callCount <= RefuseFirstNCalls)
                {
                    return Task.FromResult(new ArchiveCloneRowPage([], null, true));
                }

                List<(byte[] Key, byte[] Value)> matching = _rows.TryGetValue(column, out List<(byte[] Key, byte[] Value)>? list)
                    ? list.Where(r => Bytes.BytesComparer.Compare(r.Key, startKey) >= 0 && Bytes.BytesComparer.Compare(r.Key, endKey) <= 0)
                        .OrderBy(r => r.Key, Bytes.Comparer!)
                        .ToList()
                    : [];

                if (cursor is not null)
                {
                    matching = matching.Where(r => Bytes.BytesComparer.Compare(r.Key, cursor) > 0).ToList();
                }

                List<HistoryRowEntry> page = matching.Take(PageSize).Select(r => new HistoryRowEntry(r.Key, r.Value)).ToList();
                byte[]? nextCursor = page.Count < matching.Count ? page[^1].Key : null;
                return Task.FromResult(new ArchiveCloneRowPage(page, nextCursor, false));
            }
        }
    }

    private sealed class FakeHeaderSource : ICloneHeaderSource
    {
        public Dictionary<ulong, ValueHash256> Roots { get; } = [];

        public ValueHash256? TryGetStateRoot(ulong block)
        {
            if (Roots.TryGetValue(block, out ValueHash256 root)) return root;
            return null;
        }
    }

    private sealed class FakePeerSink : IArchiveClonePeerSink
    {
        public List<IArchiveCloneSource> Banned { get; } = [];

        public void BanSource(IArchiveCloneSource source, string reason) => Banned.Add(source);

        public bool TryGetAlternateSource(IArchiveCloneSource banned, [NotNullWhen(true)] out IArchiveCloneSource? alternate)
        {
            alternate = null;
            return false;
        }
    }
}
