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
    public void TearDown()
    {
        _executor.Dispose();
        _columns.Dispose();
    }

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
        Block block = Build.A.Block.WithNumber(20).WithTransactions(Build.A.Transaction.TestObject).TestObject;
        _executor.Writes = (tracer) =>
        {
            tracer.StartNewBlockTrace(block);
            ITxTracer txTracer = tracer.StartNewTxTrace(block.Transactions[0]);
            txTracer.ReportBalanceChange(TestItem.AddressA, 0, 7);
            tracer.EndTxTrace();
            tracer.EndBlockTrace();
        };

        TransactionChangesetIndex index = Index();
        using TransactionChangesetBuilder builder = Builder(index);
        builder.TryBuildNext();

        bool rented = index.TryRentOverlay(20, block.Hash!, beforeTransaction: 1, out MidBlockOverlayCache.Lease lease);
        using (lease)
        {
            lease.Overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rented, Is.True);
                Assert.That(account!.Balance, Is.EqualTo((UInt256)7));
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
    public void WithWorkers_TheTipThreadLeavesTheRetrofitToThem()
    {
        Capture(upTo: 20);
        _config.HistoryTransactionIndexRetrofitFromBlock = 1;
        _config.HistoryTransactionIndexWorkers = 2;
        using TransactionChangesetBuilder builder = Builder();

        while (builder.TryBuildNext())
        {
        }

        Assert.That(_executor.Executed, Is.EqualTo(new ulong[] { 20 }).AsCollection, "with workers the tip thread only ever follows the tip");
    }

    [Test]
    public void Chunks_AreHandedOutDownwardsFromTheCoverageEdge()
    {
        Capture(upTo: 300);
        _config.HistoryTransactionIndexRetrofitFromBlock = 1;
        _config.HistoryTransactionIndexWorkers = 2;
        using TransactionChangesetBuilder builder = Builder();
        builder.TryBuildNext();

        builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk first);
        builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk second);
        builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk third);
        bool fourth = builder.TryClaimChunk(out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((first.Bottom, first.Top), Is.EqualTo((172UL, 299UL)));
            Assert.That((second.Bottom, second.Top), Is.EqualTo((44UL, 171UL)));
            Assert.That((third.Bottom, third.Top), Is.EqualTo((1UL, 43UL)), "the last chunk stops at the retrofit block");
            Assert.That(fourth, Is.False);
        }
    }

    [Test]
    public void AChunkFinishedOutOfOrder_JoinsCoverageOnlyWhenTheChunksAboveItHave()
    {
        Capture(upTo: 300);
        _config.HistoryTransactionIndexRetrofitFromBlock = 1;
        _config.HistoryTransactionIndexWorkers = 2;
        TransactionChangesetIndex index = Index();
        using TransactionChangesetBuilder builder = Builder(index);
        builder.TryBuildNext();
        builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk upper);
        builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk lower);

        builder.BuildChunk(lower, _executor);
        builder.Complete(lower);
        index.TryGetCoverage(out ulong fromAfterLower, out _);
        builder.BuildChunk(upper, _executor);
        builder.Complete(upper);
        index.TryGetCoverage(out ulong fromAfterBoth, out ulong to);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromAfterLower, Is.EqualTo(300), "a chunk with a gap above it must not be claimed, or the gap would read as indexed");
            Assert.That(fromAfterBoth, Is.EqualTo(44), "once the gap closes both chunks join at once");
            Assert.That(to, Is.EqualTo(300));
        }
    }

    [Test]
    public void AChunk_IsBuiltAscendingOnOneRun()
    {
        Capture(upTo: 300);
        _config.HistoryTransactionIndexRetrofitFromBlock = 1;
        _config.HistoryTransactionIndexWorkers = 2;
        using TransactionChangesetBuilder builder = Builder();
        builder.TryBuildNext();
        _executor.Executed.Clear();

        builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk chunk);
        builder.BuildChunk(chunk, _executor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_executor.Executed[0], Is.EqualTo(172UL), "a run starts at the bottom of its chunk and carries the state upward");
            Assert.That(_executor.Executed[^1], Is.EqualTo(299UL));
            Assert.That(_executor.Executed, Is.Ordered.Ascending);
        }
    }

    [Test]
    public void AChunkThatFailed_IsRetriedBeforeANewOneIsHandedOut()
    {
        Capture(upTo: 300);
        _config.HistoryTransactionIndexRetrofitFromBlock = 1;
        _config.HistoryTransactionIndexWorkers = 2;
        using TransactionChangesetBuilder builder = Builder();
        builder.TryBuildNext();
        _executor.Fail = true;
        bool built = builder.TryBuildNextChunk(_executor);
        _executor.Fail = false;

        builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk retried);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.False, "a chunk that could not be built is not a step forward, so the worker must back off rather than spin");
            Assert.That((retried.Bottom, retried.Top), Is.EqualTo((172UL, 299UL)), "coverage cannot cross a chunk that never completed, so it goes first");
        }
    }

    [TestCase(true, TestName = "ABuiltStep_RestsOutItsDutyCycle")]
    [TestCase(false, TestName = "AFailedStep_RestsOutItsDutyCycleAndThenIdles")]
    public void WorkDone_IsRestedOffWhetherOrNotItBuilt(bool built)
    {
        _config.HistoryTransactionIndexDutyCyclePercent = 25;
        using TransactionChangesetBuilder builder = Builder();

        TimeSpan rest = builder.RestFor(TimeSpan.FromSeconds(10), built);

        Assert.That(rest, Is.EqualTo(TimeSpan.FromSeconds(30) + (built ? TimeSpan.Zero : TransactionChangesetBuilder.IdleDelay)),
            "a chunk that fails after most of its work must still rest off that work, or the duty cycle is silently 100%");
    }

    [Test]
    public void AStepThatCostNothingAndBuiltNothing_StillIdles()
    {
        _config.HistoryTransactionIndexDutyCyclePercent = 100;
        using TransactionChangesetBuilder builder = Builder();

        Assert.That(builder.RestFor(TimeSpan.Zero, built: false), Is.EqualTo(TransactionChangesetBuilder.IdleDelay));
    }

    [Test]
    public void AChunkThatKeepsFailing_IsNeverDropped()
    {
        Capture(upTo: 300);
        _config.HistoryTransactionIndexRetrofitFromBlock = 1;
        _config.HistoryTransactionIndexWorkers = 2;
        using TransactionChangesetBuilder builder = Builder();
        builder.TryBuildNext();
        _executor.Fail = true;

        for (int attempt = 0; attempt < TransactionChangesetBuilder.WarnAfterAttempts + 2; attempt++) builder.TryBuildNextChunk(_executor);
        _executor.Fail = false;
        builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk retried);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((retried.Bottom, retried.Top), Is.EqualTo((172UL, 299UL)), "coverage can never cross a dropped chunk, so a failing one stays in the queue");
            Assert.That(retried.Attempts, Is.EqualTo(TransactionChangesetBuilder.WarnAfterAttempts + 2));
        }
    }

    [Test]
    public void PastTheCap_NoNewChunkIsHandedOut_UntilTheFailingOneBuilds()
    {
        Capture(upTo: 300);
        _config.HistoryTransactionIndexRetrofitFromBlock = 1;
        _config.HistoryTransactionIndexWorkers = 2;
        using TransactionChangesetBuilder builder = Builder();
        builder.TryBuildNext();
        _executor.Fail = true;
        for (int attempt = 0; attempt < TransactionChangesetBuilder.WarnAfterAttempts; attempt++) builder.TryBuildNextChunk(_executor);

        bool retryHandedOut = builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk failing);
        bool freshHandedOut = builder.TryClaimChunk(out _);
        _executor.Fail = false;
        builder.Complete(new TransactionChangesetBuilder.Chunk(1, 43));
        bool stillStalled = !builder.TryClaimChunk(out _);
        builder.BuildChunk(failing, _executor);
        builder.Complete(failing);
        bool resumed = builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk next);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retryHandedOut, Is.True, "the failing chunk keeps being retried");
            Assert.That(freshHandedOut, Is.False, "rows below a chunk that cannot build could never be claimed, so the other workers must not write them");
            Assert.That(stillStalled, Is.True, "another worker finishing a chunk of its own does not make the failing one buildable");
            Assert.That(resumed, Is.True, "once the failing chunk itself builds, chunks are handed out again");
            Assert.That(next.Top, Is.EqualTo(failing.Bottom - 1));
        }
    }

    [Test]
    public void AChunkWhoseBuildThrew_IsRequeuedRatherThanLost()
    {
        Capture(upTo: 300);
        _config.HistoryTransactionIndexRetrofitFromBlock = 1;
        _config.HistoryTransactionIndexWorkers = 2;
        using TransactionChangesetBuilder builder = Builder();
        builder.TryBuildNext();
        _executor.Throw = true;

        Assert.That(() => builder.TryBuildNextChunk(_executor), Throws.InvalidOperationException);
        _executor.Throw = false;
        builder.TryClaimChunk(out TransactionChangesetBuilder.Chunk retried);

        Assert.That((retried.Bottom, retried.Top), Is.EqualTo((172UL, 299UL)), "a chunk that failed with an exception must come back, or coverage never crosses it");
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

    private sealed class RecordingExecutor : IHistoryBlockExecutor, IHistoryBlockExecutorFactory
    {
        public List<ulong> Executed { get; } = [];

        public bool Fail { get; set; }

        public bool Throw { get; set; }

        public Action<IBlockTracer>? Writes { get; set; }

        public IHistoryBlockExecutor Create() => this;

        public IHistoryBlockRun? BeginRun(ulong firstBlock) => Fail ? null : new Run(this, firstBlock);

        public bool TryExecute(ulong block, IBlockTracer tracer, CancellationToken cancellationToken)
        {
            if (Throw) throw new InvalidOperationException("replay failed");
            if (Fail) return false;

            lock (Executed)
            {
                Executed.Add(block);
            }

            if (Writes is { } writes) writes(tracer);
            else
            {
                tracer.StartNewBlockTrace(Build.A.Block.WithNumber(block).TestObject);
                tracer.EndBlockTrace();
            }

            return true;
        }

        public void Dispose()
        {
        }

        private sealed class Run(RecordingExecutor executor, ulong first) : IHistoryBlockRun
        {
            private ulong _next = first;

            public bool TryExecuteNext(IBlockTracer tracer, CancellationToken cancellationToken) => executor.TryExecute(_next++, tracer, cancellationToken);

            public void Dispose()
            {
            }
        }
    }
}
