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
        _db.Dispose();
        _historyColumns.Dispose();
    }

    private sealed class FakeHeaders : IHistoryHeaderSource
    {
        public Dictionary<ulong, ValueHash256> Roots { get; } = [];

        public ValueHash256? TryGetStateRoot(ulong block) => Roots.TryGetValue(block, out ValueHash256 root) ? root : null;
    }

    private (HistoryAvailability Availability, HistoryRowFormat RowFormat) CreateShared(FlatDbConfig config)
    {
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        return (availability, HistoryRowFormat.Resolve(availability, config));
    }

    private HistoryWalkVerificationCoordinator CreateCoordinator(FlatDbConfig config, FakeHeaders headers)
    {
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = CreateShared(config);
        return new HistoryWalkVerificationCoordinator(
            _db, _historyColumns, headers, availability, rowFormat, config, new ArchiveProofRetrofit(_historyColumns, CommitmentDepthPolicy.Default, new CommitmentMetadata(_historyColumns), new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance), LimboLogs.Instance), new CommitmentMetadata(_historyColumns), LimboLogs.Instance, pollDelay: TimeSpan.FromMilliseconds(10));
    }

    [Test]
    public void WhenTheFlagIsOff_NeverStarts()
    {
        using HistoryWalkVerificationCoordinator coordinator = CreateCoordinator(
            new FlatDbConfig { HistoryEnabled = true }, new FakeHeaders());

        Assert.That(coordinator.Started, Is.False, "Flat.HistoryVerifyEveryBlock defaults to off; nothing may run uninvited");
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

        using HistoryWalkVerificationCoordinator coordinator = new(
            _db, _historyColumns, headers, availability, rowFormat, config, new ArchiveProofRetrofit(_historyColumns, CommitmentDepthPolicy.Default, new CommitmentMetadata(_historyColumns), new ArchiveProofSettings(config, rowFormat, LimboLogs.Instance), LimboLogs.Instance), new CommitmentMetadata(_historyColumns), LimboLogs.Instance, pollDelay: TimeSpan.FromMilliseconds(10));
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
            Assert.That(tight.Workers, Is.EqualTo(1), "with two gigabytes of headroom only one worker fits its budget, never zero");
            Assert.That((pinned.Workers, pinned.RowsPerPartition), Is.EqualTo((3, 100L)), "explicit settings are honoured as given");
        }
    }

    [Test]
    public void WhenAskedToVerifyAWindowedDatabase_RefusesAtConstruction() =>
        Assert.That(
            () => CreateCoordinator(
                new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 100, HistoryVerifyEveryBlock = true }, new FakeHeaders()),
            Throws.InstanceOf<InvalidConfigurationException>(),
            "asking for a verification the windowed mode cannot deliver must fail loudly at startup, exactly when the operator asked for it");
}
