// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class TransactionChangesetBuilderTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _columns = null!;
    private HistoryAvailability _availability = null!;
    private FlatDbConfig _config = null!;
    private RecordingExecutor _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _columns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _availability = new HistoryAvailability(_columns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        _config = new FlatDbConfig { HistoryTransactionIndexEnabled = true, HistoryTransactionIndexDutyCyclePercent = 100 };
        _executor = new RecordingExecutor();
    }

    [TearDown]
    public void TearDown() => _columns.Dispose();

    [Test]
    public void TheFirstBlockBuilt_IsTheWatermark()
    {
        Capture(upTo: 20);
        using TransactionChangesetBuilder builder = Builder();

        bool built = builder.TryBuildNext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.True);
            Assert.That(_executor.Executed, Is.EqualTo(new ulong[] { 20 }), "a node turning the index on indexes from where it is, not from genesis");
        }
    }

    [Test]
    public void TheBuilder_NeverRunsPastTheHistoryWatermark()
    {
        Capture(upTo: 20);
        using TransactionChangesetBuilder builder = Builder();

        builder.TryBuildNext();
        bool second = builder.TryBuildNext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.False, "block 21 has no captured history to resolve its reads against");
            Assert.That(_executor.Executed, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void TheBuilder_ResumesFromItsCoverage()
    {
        Capture(upTo: 20);
        using (TransactionChangesetBuilder first = Builder())
        {
            first.TryBuildNext();
        }

        Capture(upTo: 22);
        using TransactionChangesetBuilder restarted = Builder();
        restarted.TryBuildNext();
        restarted.TryBuildNext();

        Assert.That(_executor.Executed, Is.EqualTo(new ulong[] { 20, 21, 22 }).AsCollection,
            "coverage is one contiguous range, so a restart continues it rather than jumping to the new watermark");
    }

    [Test]
    public void ABlockThatWillNotExecute_LeavesCoverageWhereItWas()
    {
        Capture(upTo: 20);
        _executor.Fail = true;
        using TransactionChangesetBuilder builder = Builder();

        bool built = builder.TryBuildNext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.False);
            Assert.That(Index().TryGetCoverage(out _, out _), Is.False, "a block that could not be re-executed must not be claimed as indexed");
        }
    }

    [Test]
    public void TheRowsOfABuiltBlock_AreReadableThroughTheOverlay()
    {
        Capture(upTo: 20);
        _executor.Writes = (tracer) =>
        {
            tracer.StartNewBlockTrace(Build.A.Block.WithNumber(20).TestObject);
            ITxTracer txTracer = tracer.StartNewTxTrace(null);
            txTracer.ReportBalanceChange(TestItem.AddressA, 0, 7);
            tracer.EndTxTrace();
            tracer.EndBlockTrace();
        };

        TransactionChangesetIndex index = Index();
        using TransactionChangesetBuilder builder = Builder(index);
        builder.TryBuildNext();

        bool rented = index.TryRentOverlay(20, beforeTransaction: 1, out MidBlockOverlayCache.Lease lease);
        using (lease)
        {
            lease.Overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay account);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rented, Is.True);
                Assert.That(account.Balance, Is.EqualTo((UInt256)7));
            }
        }
    }

    [Test]
    public void OnceCaughtUp_TheBuilderWalksBackwardsToTheConfiguredBlock()
    {
        Capture(upTo: 20);
        _config.HistoryTransactionIndexRetrofitFromBlock = 18;
        using TransactionChangesetBuilder builder = Builder();

        while (builder.TryBuildNext())
        {
        }

        Assert.That(_executor.Executed, Is.EqualTo(new ulong[] { 20, 19, 18 }).AsCollection,
            "the tip first, then older blocks down to the configured one and no further");
    }

    [Test]
    public void TheBackwardsWalk_StopsAtTheHistoryFloor()
    {
        Capture(upTo: 20);
        _availability.PublishGlobalFloor(19);
        _config.HistoryTransactionIndexRetrofitFromBlock = 1;
        using TransactionChangesetBuilder builder = Builder();

        while (builder.TryBuildNext())
        {
        }

        Assert.That(_executor.Executed, Is.EqualTo(new ulong[] { 20, 19 }).AsCollection,
            "a block whose history is pruned cannot be re-executed against its parent");
    }

    [Test]
    public void WithoutARetrofitBlock_TheBuilderNeverWalksBackwards()
    {
        Capture(upTo: 20);
        using TransactionChangesetBuilder builder = Builder();

        while (builder.TryBuildNext())
        {
        }

        Assert.That(_executor.Executed, Is.EqualTo(new ulong[] { 20 }).AsCollection);
    }

    [Test]
    public void TheBuilder_DoesNothingWhenTheIndexIsOff()
    {
        Capture(upTo: 20);
        _config.HistoryTransactionIndexEnabled = false;
        using TransactionChangesetBuilder builder = Builder();

        builder.Start();
        bool built = builder.TryBuildNext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.False);
            Assert.That(_executor.Executed, Is.Empty);
        }
    }

    private TransactionChangesetIndex Index() => new(_columns, _config);

    private TransactionChangesetBuilder Builder() => Builder(Index());

    private TransactionChangesetBuilder Builder(TransactionChangesetIndex index) =>
        new(index, _executor, _availability, _config, LimboLogs.Instance);

    private void Capture(ulong upTo)
    {
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _columns.StartWriteBatch())
        {
            for (ulong block = 0; block <= upTo; block++)
            {
                HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), block, ValueKeccak.Zero, HistoryAvailability.FormatVersion);
            }
        }

        _availability.PublishWatermark(upTo, HistoryAvailability.FormatVersion);
    }

    private sealed class RecordingExecutor : IHistoryBlockExecutor
    {
        public List<ulong> Executed { get; } = [];

        public bool Fail { get; set; }

        public Action<IBlockTracer>? Writes { get; set; }

        public bool TryExecute(ulong block, IBlockTracer tracer, CancellationToken cancellationToken)
        {
            if (Fail) return false;

            Executed.Add(block);
            Writes?.Invoke(tracer);
            return true;
        }
    }
}
