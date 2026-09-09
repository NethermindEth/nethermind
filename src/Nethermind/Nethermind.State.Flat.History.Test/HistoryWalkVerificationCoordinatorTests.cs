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
        metadata.BeginWalk(0, 100, HistoryWalkRun.WorkItems);
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
        metadata.BeginWalk(0, 2, HistoryWalkRun.WorkItems);
        metadata.AdvanceTipSeries(3, 8, out _);

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config,
            CreateRetrofit(metadata, config, rowFormat),
            metadata, LimboLogs.Instance, TimeSpan.FromMilliseconds(10));

        availability.PublishWatermark(8, rowFormat.FormatVersion);

        coordinator.Start();
        await coordinator.VerificationLoop;

        Assert.That(coordinator.LastVerdict!.BlocksCompared, Is.LessThanOrEqualTo(3),
            "the walk covered blocks 0 to 2 and the tip series already commits 3 to 8, so a second walk would scan the whole key space to find a handful of blocks it does not need to build");
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
        metadata.BeginWalk(4, 8, HistoryWalkRun.WorkItems);
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
        metadata.BeginWalk(0, 8, HistoryWalkRun.WorkItems);
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
