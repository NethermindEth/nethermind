// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Init.FlatHistory;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using NUnit.Framework;

namespace Nethermind.Init.Test.FlatHistory;

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

    private sealed class FakeHeaders : ICloneHeaderSource
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
            _db, _historyColumns, headers, availability, rowFormat, config, LimboLogs.Instance, pollDelay: TimeSpan.FromMilliseconds(10));
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
        // An empty history whose every covered block carries the empty-tree root: the walk's start state is the
        // empty trie, nothing changes, and every header agrees - the smallest honest archive there is.
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
            _db, _historyColumns, headers, availability, rowFormat, config, LimboLogs.Instance, pollDelay: TimeSpan.FromMilliseconds(10));

        Assert.That(coordinator.Started, Is.True);

        HistoryWalkVerdict? verdict = null;
        for (int i = 0; i < 500 && verdict is null; i++)
        {
            await Task.Delay(10);
            verdict = coordinator.LastVerdict;
        }

        Assert.That(verdict, Is.Not.Null, "the coordinator must run the walk once the watermark exists and publish its verdict");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict!.Value.Verified, Is.True);
            Assert.That(verdict.Value.Mismatches, Is.Empty);
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
