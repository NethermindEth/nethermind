// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Walk;
using System.Linq;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class HistoryWalkVerificationCoordinatorTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (CommitmentReclaimer reclaimer in _reclaimers) reclaimer.Dispose();
        _reclaimers.Clear();
        foreach (CommitmentMetadata metadata in _metadatas) metadata.Dispose();
        _metadatas.Clear();
        _db.Dispose();
        _historyColumns.Dispose();
    }

    private sealed class FakeHeaders : IHistoryHeaderSource
    {
        public Dictionary<ulong, ValueHash256> Roots { get; } = [];

        public ValueHash256? TryGetStateRoot(ulong block) => Roots.TryGetValue(block, out ValueHash256 root) ? root : (ValueHash256?)null;
    }

    private (HistoryAvailability Availability, HistoryRowFormat RowFormat) CreateShared(FlatDbConfig config)
    {
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        return (availability, HistoryRowFormat.Resolve(availability, config));
    }

    private readonly List<CommitmentReclaimer> _reclaimers = [];
    private readonly List<CommitmentMetadata> _metadatas = [];

    private FakeHeaders CreateEmptyHeaders(HistoryRowFormat rowFormat, ulong lastBlock = 8)
    {
        ValueHash256 emptyRoot = new(Keccak.EmptyTreeHash.Bytes);
        FakeHeaders headers = new();
        using IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch();
        for (ulong block = 0; block <= lastBlock; block++)
        {
            headers.Roots[block] = emptyRoot;
            HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, emptyRoot, rowFormat.FormatVersion);
        }

        return headers;
    }

    private CommitmentMetadata CreateMetadata()
    {
        CommitmentMetadata metadata = new(_historyColumns, CommitmentDepthPolicy.Default);
        _metadatas.Add(metadata);
        return metadata;
    }

    private ArchiveProofRetrofit CreateRetrofit(CommitmentMetadata metadata, FlatDbConfig config, HistoryRowFormat rowFormat)
    {
        ArchiveProofSettings settings = new(config, rowFormat, LimboLogs.Instance);
        CommitmentReclaimer reclaimer = new(_historyColumns, CommitmentDepthPolicy.Default, metadata, settings, LimboLogs.Instance);
        _reclaimers.Add(reclaimer);
        return new ArchiveProofRetrofit(_historyColumns, CommitmentDepthPolicy.Default, metadata, settings, reclaimer, LimboLogs.Instance);
    }

    private HistoryWalkVerificationCoordinator CreateCoordinator(FlatDbConfig config, FakeHeaders headers)
    {
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        CommitmentMetadata metadata = CreateMetadata();
        return new HistoryWalkVerificationCoordinator(
            _db, _historyColumns, headers, availability, rowFormat, config, CreateRetrofit(metadata, config, rowFormat), metadata, LimboLogs.Instance, pollDelay: TimeSpan.FromMilliseconds(10));
    }

    [Test]
    public void WhenTheFlagIsOff_NeverStarts()
    {
        using HistoryWalkVerificationCoordinator coordinator = CreateCoordinator(
            new FlatDbConfig { HistoryEnabled = true }, new FakeHeaders());

        Assert.That(coordinator.Started, Is.False, "Flat.HistoryVerifyEveryBlock defaults to off; nothing may run uninvited");
    }

    [Test]
    public void WhenTheFlagIsOff_ALeftoverWalkCheckpointIsAbandoned_SoReclaimIsNotHeldBack()
    {
        FlatDbConfig config = new() { HistoryEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        using CommitmentMetadata metadata = new(_historyColumns, CommitmentDepthPolicy.Default);
        metadata.BeginWalk(0, 100, HistoryWalkRun.WorkItems, buildCommitments: false);
        using (SeriesWriter scratch = new(_historyColumns))
        {
            scratch.WriteEmpty(SeriesScope.Accounts.Key(TreePath.FromNibble([0x1, 0x2]), scratch: true), 7);
        }

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, new FakeHeaders(), availability, rowFormat, config, CreateRetrofit(metadata, config, rowFormat), metadata, LimboLogs.Instance, pollDelay: TimeSpan.FromMilliseconds(10));

        coordinator.Start();

        bool reclaimed = metadata.TryReclaimOutsideWalk(() => { });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.TryGetWalkInProgress(out _, out _), Is.False, "a checkpoint nobody will resume must not survive a start with the flag off, or it holds reclaim back across every restart");
            Assert.That(reclaimed, Is.True);
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments).GetAll().Any(row => row.Key[0] == SeriesKey.ScratchMarker), Is.False, "the interrupted walk's scratch series are the expensive half of the checkpoint and nothing else ever reclaims them");
        }
    }

    [Test]
    public async Task WhenTheWatermarkAppears_RunsTheWalkOnceAndReportsTheVerdict()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true, HistoryVerifySegments = 2 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        ValueHash256 emptyRoot = new(Keccak.EmptyTreeHash.Bytes);
        FakeHeaders headers = new();
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            for (ulong block = 0; block <= 2; block++)
            {
                headers.Roots[block] = emptyRoot;
                HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, emptyRoot, rowFormat.FormatVersion);
            }
        }

        availability.PublishWatermark(2, rowFormat.FormatVersion);

        CommitmentMetadata metadata = CreateMetadata();
        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config, CreateRetrofit(metadata, config, rowFormat), metadata, LimboLogs.Instance, pollDelay: TimeSpan.FromMilliseconds(10));
        coordinator.Start();

        Assert.That(coordinator.Started, Is.True);

        await coordinator.VerificationLoop;
        HistoryWalkVerdict? verdict = coordinator.LastVerdict;

        Assert.That(verdict, Is.Not.Null, "the coordinator must run the walk once the watermark exists and publish its verdict");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict!.Verified, Is.True);
            Assert.That(verdict!.Mismatches, Is.Empty);
        }
    }

    [Test]
    public async Task WhenTheTipAlreadyCoversTheBlocksCapturedMeanwhile_TheWalkDoesNotRunAgain()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true, ArchiveProofBuildEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        FakeHeaders headers = CreateEmptyHeaders(rowFormat);

        CommitmentMetadata metadata = new(_historyColumns, CommitmentDepthPolicy.Default);
        metadata.BeginWalk(0, 2, HistoryWalkRun.WorkItems, buildCommitments: true);
        metadata.AdvanceTipSeries(3, 8, out _);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat),
            metadata, LimboLogs.Instance, TimeSpan.FromMilliseconds(10));

        availability.PublishWatermark(8, rowFormat.FormatVersion);

        coordinator.Start();
        await coordinator.VerificationLoop;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coordinator.LastVerdict!.BlocksCompared, Is.LessThanOrEqualTo(3),
                "the walk covered blocks 0 to 2 and the tip series already commits 3 to 8, so a second walk would scan the whole key space to find a handful of blocks it does not need to build");
            Assert.That(metadata.TryGetCoverage(out ulong coveredFrom, out ulong coveredTo) && coveredFrom == 0 && coveredTo == 8, Is.True,
                "the walk's publish joins the tip series it touches, so blocks 3 to 8 serve now rather than after whatever capture happens next");
        }
    }

    [Test]
    public async Task AFinishedWalkWhoseTailTheTipCommitted_IsNotRunAgainOnRestart()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true, ArchiveProofBuildEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        FakeHeaders headers = CreateEmptyHeaders(rowFormat);

        CommitmentMetadata metadata = CreateMetadata();
        metadata.TryPublishVerifiedCoverage(0, 2, out _, out _);
        metadata.AdvanceTipSeries(3, 8, out _);
        availability.PublishWatermark(8, rowFormat.FormatVersion);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat),
            metadata, LimboLogs.Instance, TimeSpan.FromMilliseconds(10));

        coordinator.Start();
        await coordinator.VerificationLoop;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coordinator.LastVerdict, Is.Null,
                "the earlier run verified 0 to 2 and the tip committed 3 to 8; a restart with the flag still on must not walk the chain again from genesis");
            Assert.That(metadata.TryGetWalkInProgress(out _, out _), Is.False);
        }
    }

    [Test]
    public async Task AVerifyOnlyRunDoesNotStandInForABuild_WhenBuildingIsEnabledLater()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true, ArchiveProofBuildEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        FakeHeaders headers = CreateEmptyHeaders(rowFormat);

        CommitmentMetadata metadata = CreateMetadata();
        metadata.MarkWalkVerified(0, 8);
        availability.PublishWatermark(8, rowFormat.FormatVersion);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat),
            metadata, LimboLogs.Instance, TimeSpan.FromMilliseconds(10));

        coordinator.Start();
        await coordinator.VerificationLoop;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coordinator.LastVerdict, Is.Not.Null, "an earlier verify-only run proved the rows but built no commitments; enabling the build must walk again to build them");
            Assert.That(metadata.TryGetCoverage(out ulong from, out ulong to) && from == 0 && to == 8, Is.True);
        }
    }

    [Test]
    public async Task AnUnfinishedBuildTail_DoesNotSkipTheUnbuiltProofPrefix(
        [Values(4UL, 512UL)] ulong pendingFrom, [Values] bool tipCoversTail)
    {
        ulong watermark = pendingFrom + 4;
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true, ArchiveProofBuildEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        FakeHeaders headers = CreateEmptyHeaders(rowFormat, watermark);
        CommitmentMetadata metadata = CreateMetadata();
        metadata.MarkWalkVerified(0, pendingFrom - 1);
        metadata.BeginWalk(pendingFrom, watermark, HistoryWalkRun.WorkItems, buildCommitments: true);
        if (tipCoversTail) metadata.AdvanceTipSeries(pendingFrom, watermark, out _);
        availability.PublishWatermark(watermark, rowFormat.FormatVersion);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat), metadata, LimboLogs.Instance, TimeSpan.FromMilliseconds(10));

        coordinator.Start();
        await coordinator.VerificationLoop;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coordinator.LastVerdict?.Verified, Is.True);
            Assert.That(metadata.TryGetCoverage(out ulong from, out ulong to), Is.True,
                "verification-only progress cannot replace proof coverage, even when the tip committed the pending tail");
            Assert.That(from, Is.Zero);
            Assert.That(to, Is.EqualTo(watermark));
            Assert.That(metadata.TryGetWalkInProgress(out _, out _), Is.False);
        }
    }

    [Test]
    public void WalkCheckpoint_ReusesCompletedItemsOnlyInTheSameMode(
        [Values] bool previousBuild, [Values] bool nextBuild)
    {
        CommitmentMetadata metadata = CreateMetadata();
        metadata.BeginWalk(0, 8, HistoryWalkRun.WorkItems, buildCommitments: previousBuild);
        metadata.MarkWalkItemDone(HistoryWalkRun.WorkItems - 1, []);

        metadata.BeginWalk(0, 8, HistoryWalkRun.WorkItems, buildCommitments: nextBuild);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.WalkModeMatches(nextBuild), Is.True);
            Assert.That(metadata.IsWalkItemDone(HistoryWalkRun.WorkItems - 1), Is.EqualTo(previousBuild == nextBuild));
        }
        metadata.ClearWalk(HistoryWalkRun.WorkItems);
        Assert.That(metadata.WalkModeMatches(nextBuild), Is.False);
    }

    [Test]
    public async Task InterruptedWalk_WithIncompatibleMode_RestartsTheRequestedBuildRange([Values] bool legacyCheckpoint)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true, ArchiveProofBuildEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        FakeHeaders headers = CreateEmptyHeaders(rowFormat);
        CommitmentMetadata metadata = CreateMetadata();
        metadata.BeginWalk(0, 2, HistoryWalkRun.WorkItems, buildCommitments: legacyCheckpoint);
        metadata.MarkWalkItemDone(HistoryWalkRun.WorkItems - 1, []);
        if (legacyCheckpoint)
            WalkCheckpointTestHelper.RemoveMode(_historyColumns, metadata);
        availability.PublishWatermark(8, rowFormat.FormatVersion);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat), metadata, LimboLogs.Instance);

        coordinator.Start();
        await coordinator.VerificationLoop;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coordinator.LastVerdict?.Verified, Is.True);
            Assert.That(coordinator.LastVerdict?.BlocksCompared, Is.EqualTo(9UL));
            Assert.That(metadata.TryGetCoverage(out ulong from, out ulong to), Is.True);
            Assert.That(from, Is.Zero);
            Assert.That(to, Is.EqualTo(8UL));
        }
    }

    [Test]
    public async Task InterruptedWalk_WithCompletedPrefix_RestartsOnlyTheUnfinishedTail(
        [Values] bool buildCommitments, [Values] bool legacyCheckpoint, [Values] bool fullyCovered)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true, ArchiveProofBuildEnabled = buildCommitments };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        const ulong watermark = 520;
        const ulong coveredTo = 514;
        FakeHeaders headers = CreateEmptyHeaders(rowFormat, watermark);
        CommitmentMetadata metadata = CreateMetadata();
        ulong completedTo = fullyCovered ? watermark : coveredTo;
        if (buildCommitments)
            Assert.That(metadata.TryPublishVerifiedCoverage(0, completedTo, out _, out _), Is.True);
        else
            metadata.MarkWalkVerified(0, completedTo);
        metadata.BeginWalk(coveredTo + 1, watermark, HistoryWalkRun.WorkItems, buildCommitments: true);
        if (legacyCheckpoint) WalkCheckpointTestHelper.RemoveMode(_historyColumns, metadata);
        availability.PublishWatermark(watermark, rowFormat.FormatVersion);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat), metadata, LimboLogs.Instance);

        coordinator.Start();
        await coordinator.VerificationLoop;

        using (Assert.EnterMultipleScope())
        {
            if (fullyCovered)
                Assert.That(coordinator.LastVerdict, Is.Null, "completed ranges must not be replayed after discarding an incompatible checkpoint");
            else
            {
                Assert.That(coordinator.LastVerdict?.Verified, Is.True);
                Assert.That(coordinator.LastVerdict?.BlocksCompared, Is.EqualTo(buildCommitments ? 9UL : 6UL),
                    "build resumes at aligned block 512; verification resumes at block 515, never genesis");
            }
            bool hasRange = buildCommitments
                ? metadata.TryGetCoverage(out ulong from, out ulong to)
                : metadata.TryGetWalkVerified(out from, out to);
            Assert.That(hasRange, Is.True);
            Assert.That(from, Is.Zero);
            Assert.That(to, Is.EqualTo(watermark));
            Assert.That(metadata.TryGetWalkInProgress(out _, out _), Is.False);
        }
    }

    [Test]
    public async Task AFinishedWalkBelowAnUncommittedTail_ContinuesFromWhereItStopped()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        FakeHeaders headers = CreateEmptyHeaders(rowFormat);

        CommitmentMetadata metadata = CreateMetadata();
        metadata.MarkWalkVerified(0, 5);
        availability.PublishWatermark(8, rowFormat.FormatVersion);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat),
            metadata, LimboLogs.Instance, TimeSpan.FromMilliseconds(10));

        coordinator.Start();
        await coordinator.VerificationLoop;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coordinator.LastVerdict, Is.Not.Null);
            Assert.That(coordinator.LastVerdict!.BlocksCompared, Is.EqualTo(3UL), "only blocks 6 to 8 are unverified, so only they are walked");
            Assert.That(metadata.TryGetWalkVerified(out ulong from, out ulong to) && from == 0 && to == 8, Is.True, "a verify-only run records what it verified, or the next restart walks the chain again from genesis");
        }
    }

    [Test]
    public async Task AnUnfinishedWalkOverBlocksTheTipCommitted_IsDroppedRatherThanResumed()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true, ArchiveProofBuildEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        ValueHash256 emptyRoot = new(Keccak.EmptyTreeHash.Bytes);
        FakeHeaders headers = new();
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            for (ulong block = 0; block <= 8; block++)
            {
                headers.Roots[block] = emptyRoot;
                HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, emptyRoot, rowFormat.FormatVersion);
            }
        }

        CommitmentMetadata metadata = new(_historyColumns, CommitmentDepthPolicy.Default);
        metadata.TryPublishVerifiedCoverage(0, 3, out _, out _);
        metadata.BeginWalk(4, 8, HistoryWalkRun.WorkItems, buildCommitments: false);
        metadata.AdvanceTipSeries(2, 8, out _);
        using (SeriesWriter scratch = new(_historyColumns))
        {
            scratch.WriteEmpty(SeriesScope.Accounts.Key(TreePath.FromNibble([0x1, 0x2]), scratch: true), 7);
        }

        availability.PublishWatermark(8, rowFormat.FormatVersion);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat),
            metadata, LimboLogs.Instance, TimeSpan.FromMilliseconds(10));

        coordinator.Start();
        await coordinator.VerificationLoop;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coordinator.LastVerdict, Is.Null, "an interrupted catch-up whose blocks the tip has already committed must be dropped, not resumed: resuming costs a scan of the whole key space to find blocks that are already there");
            Assert.That(metadata.TryGetWalkInProgress(out _, out _), Is.False, "and its marks must go, or the next restart resumes it again");
            Assert.That(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountCommitments).GetAllKeys().Any(static key => key[0] == SeriesKey.ScratchMarker), Is.False,
                "the series the interrupted run wrote are unreferenced once it is dropped; nothing else deletes them if the operator turns the flag off afterwards");
        }
    }

    [Test]
    public async Task AnUnfinishedWalkOverBlocksNoWalkHasVerified_IsResumedRatherThanDropped()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryVerifyEveryBlock = true, ArchiveProofBuildEnabled = true };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        ValueHash256 emptyRoot = new(Keccak.EmptyTreeHash.Bytes);
        FakeHeaders headers = new();
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            for (ulong block = 0; block <= 8; block++)
            {
                headers.Roots[block] = emptyRoot;
                HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, emptyRoot, rowFormat.FormatVersion);
            }
        }

        CommitmentMetadata metadata = new(_historyColumns, CommitmentDepthPolicy.Default);
        metadata.BeginWalk(0, 8, HistoryWalkRun.WorkItems, buildCommitments: false);
        metadata.AdvanceTipSeries(0, 8, out _);
        availability.PublishWatermark(8, rowFormat.FormatVersion);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat),
            metadata, LimboLogs.Instance, TimeSpan.FromMilliseconds(10));

        coordinator.Start();
        await coordinator.VerificationLoop;

        Assert.That(coordinator.LastVerdict?.Verified, Is.True,
            "the tip series brackets every range the coordinator picks once the retained floor has risen above the block the tip started at, so dropping on that alone would leave a first walk, and the epoch snapshots pruning needs, permanently unwritten");
    }

    [Test]
    public void WalkResources_UseTheCoresLeftAfterTheNodeAndTheMemoryTheBudgetLeaves()
    {
        FlatDbConfig auto = new() { HistoryEnabled = true };
        WalkResources roomy = WalkResources.Resolve(auto, processorCount: 8, totalMemoryBytes: 32L << 30, workingSetBytes: 4L << 30);
        WalkResources tight = WalkResources.Resolve(auto, processorCount: 8, totalMemoryBytes: 32L << 30, workingSetBytes: 26L << 30);
        WalkResources pinned = WalkResources.Resolve(new FlatDbConfig { HistoryEnabled = true, HistoryVerifySegments = 3, HistoryVerifyMaxRows = 100 }, processorCount: 8, totalMemoryBytes: 32L << 30, workingSetBytes: 4L << 30);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roomy.Workers, Is.EqualTo(6), "two cores stay with block processing; memory allows the rest");
            Assert.That(roomy.RowsPerPartition, Is.EqualTo(WalkResources.DefaultRowsPerPartition));
            Assert.That(tight.Workers, Is.EqualTo(1), "with four gigabytes of headroom only one worker fits its two and a half, never zero");
            Assert.That((pinned.Workers, pinned.RowsPerPartition), Is.EqualTo((3, 100L)), "explicit settings are honoured as given");
        }
    }

    [Test]
    public void WhenAskedToVerifyAWindowedDatabase_RefusesAtConstruction() =>
        Assert.That(
            () => CreateCoordinator(
                new FlatDbConfig { HistoryEnabled = true, HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 100, HistoryVerifyEveryBlock = true }, new FakeHeaders()),
            Throws.InstanceOf<InvalidConfigurationException>(),
            "asking for a verification the windowed mode cannot deliver must fail loudly at startup, exactly when the operator asked for it");
}
