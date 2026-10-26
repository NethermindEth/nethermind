// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class ProgressTrackerTests
{
    [Test]
    [Repeat(3)]
    public async Task Did_not_have_race_issue()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 1);
        progressTracker.EnqueueNextSlot(new StorageRange()
        {
            Accounts = ArrayPoolList<PathWithAccount>.Empty(),
        });

        int loopIteration = 100000;
        Task requestTask = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < loopIteration; i++)
            {
                bool finished = progressTracker.IsFinished(out SnapSyncBatch? snapSyncBatch);
                Assert.That(finished, Is.False);
                progressTracker.EnqueueNextSlot(snapSyncBatch!.StorageRangeRequest!);
            }
        });

        Task checkTask = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < loopIteration; i++)
            {
                Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.False);
            }
        });

        await requestTask;
        await checkTask;
    }

    [Test]
    public void Will_create_multiple_get_address_range_request()
    {
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 4);

        Hash256[] expectedStarts =
        [
            new("0x0000000000000000000000000000000000000000000000000000000000000000"),
            new("0x4000000000000000000000000000000000000000000000000000000000000000"),
            new("0x8000000000000000000000000000000000000000000000000000000000000000"),
            new("0xc000000000000000000000000000000000000000000000000000000000000000"),
        ];
        Hash256[] expectedLimits =
        [
            new("0x3fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            new("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            new("0xbfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
        ];

        for (int i = 0; i < 4; i++)
        {
            bool finished = progressTracker.IsFinished(out SnapSyncBatch? request);
            Assert.That(request!.AccountRangeRequest, Is.Not.Null);
            Assert.That(request.AccountRangeRequest!.StartingHash, Is.EqualTo(expectedStarts[i]));
            Assert.That(request.AccountRangeRequest.LimitHash!.Value, Is.EqualTo(expectedLimits[i]));
            Assert.That(finished, Is.False);
            request.Dispose();
        }

        bool finalFinished = progressTracker.IsFinished(out SnapSyncBatch? finalRequest);
        Assert.That(finalRequest, Is.Null);
        Assert.That(finalFinished, Is.False);
    }

    [Test]
    public void Will_deque_code_request_if_high_even_if_storage_queue_is_not_empty()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();

        for (int i = 0; i < ProgressTracker.HIGH_STORAGE_QUEUE_SIZE - 1; i++)
        {
            progressTracker.EnqueueAccountStorage(new PathWithAccount()
            {
                Path = TestItem.ValueKeccaks[0]
            });
        }

        for (int i = 0; i < ProgressTracker.HIGH_CODES_QUEUE_SIZE; i++)
        {
            progressTracker.EnqueueCodeHashes(new[] { TestItem.ValueKeccaks[0] });
        }

        progressTracker.IsFinished(out SnapSyncBatch? request);
        Assert.That(request!.CodesRequest, Is.Not.Null);
        Assert.That(request.StorageRangeRequest, Is.Null);
        request.Dispose();
    }

    [Test]
    public void Will_deque_storage_request_if_high()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();

        for (int i = 0; i < ProgressTracker.HIGH_STORAGE_QUEUE_SIZE; i++)
        {
            progressTracker.EnqueueAccountStorage(new PathWithAccount()
            {
                Path = TestItem.ValueKeccaks[0]
            });
        }

        for (int i = 0; i < ProgressTracker.HIGH_CODES_QUEUE_SIZE; i++)
        {
            progressTracker.EnqueueCodeHashes(new[] { TestItem.ValueKeccaks[0] });
        }

        progressTracker.IsFinished(out SnapSyncBatch? request);
        Assert.That(request!.CodesRequest, Is.Null);
        Assert.That(request.StorageRangeRequest, Is.Not.Null);
        request.Dispose();
    }

    // Healing is not checkpointed, so once it may start a restart must find no checkpoint to resume. A backend that
    // cannot resume is never checkpointed at all.
    [Test]
    [NonParallelizable]
    public void Will_mark_range_phase_finished_when_ranges_drain([Values] bool snapResume, [Values] bool supportsCheckpoints)
    {
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.SupportsCheckpoints.Returns(supportsCheckpoints);
        bool checkpointed = snapResume && supportsCheckpoints;
        TestLogger logger = new();
        using ProgressTracker progressTracker = CreateProgressTracker(snapTrieFactory: snapTrieFactory, logManager: new OneLoggerLogManager(new ILogger(logger)), snapResume: snapResume);

        progressTracker.IsFinished(out SnapSyncBatch? request);
        Assert.That(request!.AccountRangeRequest, Is.Not.Null);
        progressTracker.UpdateAccountRangePartitionProgress(request.AccountRangeRequest!.LimitHash!.Value, Keccak.MaxValue, false);
        progressTracker.ReportAccountRangePartitionFinished(request.AccountRangeRequest!.LimitHash!.Value);
        request.Dispose();
        Assert.That(Metrics.SnapRangesProgress, Is.EqualTo(ProgressTracker.RangesProgressScale - 1));

        bool finished = progressTracker.IsFinished(out _);
        Assert.That(finished, Is.True);
        Assert.That(Metrics.SnapRangesProgress, Is.EqualTo(ProgressTracker.RangesProgressScale));

        snapTrieFactory.Received(1).MarkRangePhaseFinished();
        if (checkpointed) snapTrieFactory.Received(1).SaveCheckpoint(null);
        else snapTrieFactory.DidNotReceiveWithAnyArgs().SaveCheckpoint(default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.LogList, Has.Some.StartsWith("Snap         State Ranges (Phase 1 of 2): ("));
            Assert.That(logger.LogList, Has.Some.EqualTo("Snap - State Ranges (Phase 1 of 2) finished."));
        }
    }

    // Regression: a checkpoint that failed to save threw out of IsFinished, came back on every retry and so held back
    // every request until the save worked.
    [Test]
    public void A_checkpoint_that_fails_to_save_does_not_hold_back_the_requests()
    {
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.SupportsCheckpoints.Returns(true);
        snapTrieFactory.When(static f => f.SaveCheckpoint(Arg.Any<SnapCheckpoint?>())).Do(static _ => throw new IOException("No space left on device"));
        using ProgressTracker progressTracker = CreateProgressTracker(snapTrieFactory: snapTrieFactory, snapResume: true);

        SnapSyncBatch? request = null;
        Assert.That(() => progressTracker.IsFinished(out request), Throws.Nothing);
        Assert.That(request?.AccountRangeRequest, Is.Not.Null);
        request?.Dispose();
    }

    // A shutdown drops the requests in flight unanswered, so the checkpoint it saves has to count them as still to do.
    [Test]
    public void Checkpoint_at_shutdown_keeps_the_requests_dropped_in_flight()
    {
        SnapCheckpoint? saved = null;
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.SupportsCheckpoints.Returns(true);
        snapTrieFactory.When(static f => f.SaveCheckpoint(Arg.Any<SnapCheckpoint?>())).Do(call => saved = call.Arg<SnapCheckpoint?>());
        using ProgressTracker progressTracker = CreateProgressTracker(snapTrieFactory: snapTrieFactory, snapResume: true, checkpointInterval: TimeSpan.MaxValue);

        PathWithAccount withStorage = new(TestItem.ValueKeccaks[1], Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject);
        PathWithAccount toRefresh = new(TestItem.ValueKeccaks[2], Build.An.Account.WithBalance(2).TestObject);
        ValueHash256 refreshFrom = new("0x8000000000000000000000000000000000000000000000000000000000000000");
        progressTracker.EnqueueAccountStorage(withStorage);
        progressTracker.EnqueueAccountRefresh(toRefresh, refreshFrom, null);
        progressTracker.EnqueueCodeHash(TestItem.ValueKeccaks[3]);

        // A refresh, the account range, the storage and the code: all handed out, and only the code request answered.
        List<SnapSyncBatch> batches = [];
        for (int i = 0; i < 4; i++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? batch);
            batches.Add(batch!);
        }

        progressTracker.ReportRequestReleased(batches.Single(static b => b.CodesRequest is not null));
        progressTracker.SaveCheckpointAtShutdown();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved!.StorageRanges.Select(static r => r.Accounts[0].Path), Is.EqualTo(new[] { withStorage.Path }));
            Assert.That(saved.AccountsToRefresh.Select(static r => (r.PathAndAccount!.Path, r.StorageStartingHash)), Is.EqualTo(new[] { (toRefresh.Path, refreshFrom) }));
            Assert.That(saved.CodesToRetrieve, Is.Empty, "the answered code request is done");
            Assert.That(saved.Partitions.Single().MoreAccountsToRight, Is.True, "the account range in flight is requested again");
        }

        foreach (SnapSyncBatch batch in batches) batch.Dispose();
    }

    // Regression: a batch of accounts dropped in flight at a shutdown came back as one slot range, which counts one
    // request in flight but releases one per account, so the tracker never saw itself idle again and the next checkpoint
    // held every request back for good.
    [Test]
    public void A_storage_batch_dropped_at_shutdown_does_not_stall_the_resumed_run()
    {
        SnapCheckpoint? saved = null;
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.SupportsCheckpoints.Returns(true);
        snapTrieFactory.When(static f => f.SaveCheckpoint(Arg.Any<SnapCheckpoint?>())).Do(call => saved = call.Arg<SnapCheckpoint?>());
        snapTrieFactory.LoadCheckpoint().Returns(_ => saved is null ? null : SnapCheckpoint.Decode(saved.Encode()));

        using (ProgressTracker interrupted = CreateProgressTracker(snapTrieFactory: snapTrieFactory, snapResume: true, checkpointInterval: TimeSpan.MaxValue))
        {
            interrupted.IsFinished(out SnapSyncBatch? accountRange);
            ValueHash256 limit = accountRange!.AccountRangeRequest!.LimitHash!.Value;
            interrupted.UpdateAccountRangePartitionProgress(limit, Keccak.MaxValue, moreChildrenToRight: false);
            interrupted.ReportAccountRangePartitionFinished(limit);
            interrupted.ReportRequestReleased(accountRange);
            accountRange.Dispose();

            interrupted.EnqueueAccountStorage(new PathWithAccount(TestItem.ValueKeccaks[1], Build.An.Account.WithStorageRoot(TestItem.KeccakA).TestObject));
            interrupted.EnqueueAccountStorage(new PathWithAccount(TestItem.ValueKeccaks[2], Build.An.Account.WithStorageRoot(TestItem.KeccakB).TestObject));
            interrupted.IsFinished(out SnapSyncBatch? droppedInFlight);
            Assert.That(droppedInFlight!.StorageRangeRequest!.Accounts, Has.Count.EqualTo(2));
            interrupted.SaveCheckpointAtShutdown();
            droppedInFlight.Dispose();
        }

        using ProgressTracker resumed = CreateProgressTracker(snapTrieFactory: snapTrieFactory, snapResume: true);
        resumed.Reset();
        resumed.LoadProgress();
        resumed.IsFinished(out SnapSyncBatch? storage);
        resumed.ReportStorageRequestFinished(storage!.StorageRangeRequest!.Accounts.Count);
        resumed.ReportRequestReleased(storage);
        storage.Dispose();

        Assert.That(resumed.IsFinished(out SnapSyncBatch? next), Is.True, "the answered storage was the last request, so the phase is done");
        Assert.That(next, Is.Null);
    }

    // Regression: a resumed run reported its account ranges as not started, so its progress never got past the
    // partitions left to do, and a response for a partition partly done before the restart counted that part twice.
    [Test]
    [NonParallelizable]
    public void A_resumed_run_reports_the_progress_made_before_the_restart()
    {
        SnapCheckpoint? saved = null;
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.SupportsCheckpoints.Returns(true);
        snapTrieFactory.When(static f => f.SaveCheckpoint(Arg.Any<SnapCheckpoint?>())).Do(call => saved = call.Arg<SnapCheckpoint?>());
        snapTrieFactory.LoadCheckpoint().Returns(_ => saved is null ? null : SnapCheckpoint.Decode(saved.Encode()));

        ValueHash256 limit;
        using (ProgressTracker interrupted = CreateProgressTracker(accountRangePartition: 4, snapTrieFactory: snapTrieFactory, snapResume: true, checkpointInterval: TimeSpan.MaxValue))
        {
            interrupted.IsFinished(out SnapSyncBatch? accountRange);
            limit = accountRange!.AccountRangeRequest!.LimitHash!.Value;
            interrupted.UpdateAccountRangePartitionProgress(limit, new ValueHash256("0x2000000000000000000000000000000000000000000000000000000000000000"), moreChildrenToRight: true);
            interrupted.ReportAccountRangePartitionFinished(limit);
            interrupted.ReportRequestReleased(accountRange);
            accountRange.Dispose();
            interrupted.SaveCheckpointAtShutdown();
        }

        using ProgressTracker resumed = CreateProgressTracker(accountRangePartition: 4, snapTrieFactory: snapTrieFactory, snapResume: true);
        resumed.Reset();
        Assert.That(Metrics.SnapRangesProgress, Is.Zero);
        resumed.LoadProgress();
        Assert.That(Metrics.SnapRangesProgress, Is.EqualTo(1250));

        resumed.UpdateAccountRangePartitionProgress(limit, new ValueHash256("0x3000000000000000000000000000000000000000000000000000000000000000"), moreChildrenToRight: true);
        Assert.That(Metrics.SnapRangesProgress, Is.EqualTo(1875));
    }

    // Regression: a restart during the state ranges phase used to start the account ranges over from the beginning.
    [Test]
    public void A_restarted_run_resumes_from_a_checkpoint_taken_with_a_request_in_flight()
    {
        int checkpointsSaved = 0;
        SnapCheckpoint? saved = null;
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.SupportsCheckpoints.Returns(true);
        snapTrieFactory.When(static f => f.SaveCheckpoint(Arg.Any<SnapCheckpoint?>())).Do(call =>
        {
            checkpointsSaved++;
            saved = call.Arg<SnapCheckpoint?>();
        });
        // Read back through the encoding, as a restarted node reads it from disk.
        snapTrieFactory.LoadCheckpoint().Returns(_ => saved is null ? null : SnapCheckpoint.Decode(saved.Encode()));

        ValueHash256 nextAccount = new("0x4000000000000000000000000000000000000000000000000000000000000000");
        PathWithAccount withStorage = new(TestItem.ValueKeccaks[1], Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject);
        PathWithAccount toRefresh = new(TestItem.ValueKeccaks[2], Build.An.Account.WithBalance(2).TestObject);
        ValueHash256 refreshFrom = new("0x8000000000000000000000000000000000000000000000000000000000000000");
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        pivot.GetPivotHeader().Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(Keccak.EmptyTreeHash).TestObject);
        pivot.UpdatedStorages.Returns([]);

        using (ProgressTracker interrupted = CreateProgressTracker(accountRangePartition: 2, snapTrieFactory: snapTrieFactory, snapResume: true, pivot: pivot))
        {
            interrupted.IsFinished(out SnapSyncBatch? accountRange);
            interrupted.IsFinished(out SnapSyncBatch? stillInFlight);
            Assert.That(stillInFlight?.AccountRangeRequest, Is.Not.Null, "a checkpoint does not hold back the requests");

            using (interrupted.BeginResponse())
            {
                ValueHash256 limit = accountRange!.AccountRangeRequest!.LimitHash!.Value;
                interrupted.UpdateAccountRangePartitionProgress(limit, nextAccount, moreChildrenToRight: true);
                interrupted.EnqueueAccountStorage(withStorage);
                interrupted.EnqueueAccountRefresh(toRefresh, refreshFrom, null);
                interrupted.EnqueueCodeHash(TestItem.ValueKeccaks[3]);
                interrupted.ReportAccountRangePartitionFinished(limit);
                interrupted.ReportRequestReleased(accountRange);
            }

            accountRange.Dispose();

            // Taken with the second partition's account range in flight.
            interrupted.IsFinished(out SnapSyncBatch? afterCheckpoint);
            afterCheckpoint!.Dispose();
            stillInFlight.Dispose();
            Assert.That(checkpointsSaved, Is.EqualTo(3));
        }

        // No checkpoint is due in this run, so its requests can all be in flight at once.
        using ProgressTracker resumed = CreateProgressTracker(accountRangePartition: 2, snapTrieFactory: snapTrieFactory, snapResume: true, pivot: pivot, checkpointInterval: TimeSpan.MaxValue);
        resumed.Reset();
        Assert.That(pivot.UpdatedStorages, Is.Empty);
        resumed.LoadProgress();

        List<SnapSyncBatch> batches = [];
        while (batches.Count < 5 && !resumed.IsFinished(out SnapSyncBatch? batch)) batches.Add(batch!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batches.Where(static b => b.AccountRangeRequest is not null).Select(static b => b.AccountRangeRequest!.StartingHash),
                Is.EquivalentTo(new[] { nextAccount, new ValueHash256("0x8000000000000000000000000000000000000000000000000000000000000000") }));
            Assert.That(batches.Single(static b => b.AccountsToRefreshRequest is not null).AccountsToRefreshRequest!.Paths[0].StorageStartingHash, Is.EqualTo(refreshFrom));
            Assert.That(batches.Single(static b => b.StorageRangeRequest is not null).StorageRangeRequest!.Accounts[0].Account!.StorageRoot, Is.EqualTo(TestItem.KeccakA));
            Assert.That(batches.Single(static b => b.CodesRequest is not null).CodesRequest, Is.EqualTo(new[] { TestItem.ValueKeccaks[3] }));
            Assert.That(pivot.UpdatedStorages, Is.EquivalentTo(new[] { toRefresh.Path.ToCommitment() }), "the account to heal survives the restart");
        }

        foreach (SnapSyncBatch batch in batches) batch.Dispose();
    }

    // A response half handled would leave the checkpoint with its range neither still to do nor recorded as done.
    [Test]
    public void A_checkpoint_waits_for_the_response_being_handled()
    {
        int checkpointsSaved = 0;
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.SupportsCheckpoints.Returns(true);
        snapTrieFactory.When(static f => f.SaveCheckpoint(Arg.Any<SnapCheckpoint?>())).Do(_ => Interlocked.Increment(ref checkpointsSaved));
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 2, snapTrieFactory: snapTrieFactory, snapResume: true);
        progressTracker.IsFinished(out SnapSyncBatch? first);

        Task<SnapSyncBatch?> next;
        using (progressTracker.BeginResponse())
        {
            next = Task.Run(() =>
            {
                progressTracker.IsFinished(out SnapSyncBatch? batch);
                return batch;
            });

            Assert.That(next.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "the checkpoint is due, so it waits for the response");
            Assert.That(Volatile.Read(ref checkpointsSaved), Is.EqualTo(1));
        }

        Assert.That(next.Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(Volatile.Read(ref checkpointsSaved), Is.EqualTo(2));
        first!.Dispose();
        next.Result!.Dispose();
    }

    [Test]
    [NonParallelizable]
    public void Will_publish_covered_account_key_space()
    {
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 4, snapTrieFactory: snapTrieFactory);
        Assert.That(Metrics.SnapRangesProgress, Is.Zero);

        progressTracker.IsFinished(out SnapSyncBatch? request);
        using (request)
        {
            ValueHash256 limit = request!.AccountRangeRequest!.LimitHash!.Value;
            progressTracker.UpdateAccountRangePartitionProgress(limit, new Hash256("0x2000000000000000000000000000000000000000000000000000000000000000"), true);
            Assert.That(Metrics.SnapRangesProgress, Is.EqualTo(1250));

            progressTracker.UpdateAccountRangePartitionProgress(limit, limit, false);
            Assert.That(Metrics.SnapRangesProgress, Is.EqualTo(2500));
        }

        snapTrieFactory.IsRangePhaseFinished().Returns(true);
        progressTracker.LoadProgress();
        Assert.That(Metrics.SnapRangesProgress, Is.EqualTo(ProgressTracker.RangesProgressScale));

        progressTracker.Reset();
        Assert.That(Metrics.SnapRangesProgress, Is.Zero);
    }

    [Test]
    public void Will_skip_account_ranges_when_range_phase_already_finished()
    {
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.IsRangePhaseFinished().Returns(true);
        TestLogger logger = new();
        using ProgressTracker progressTracker = CreateProgressTracker(snapTrieFactory: snapTrieFactory, logManager: new OneLoggerLogManager(new ILogger(logger)));

        progressTracker.LoadProgress();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? request), Is.True);
        Assert.That(request, Is.Null);
        Assert.That(logger.LogList, Has.Some.EqualTo("Snap - State Ranges (Phase 1 of 2) is finished."));
    }

    // Regression: account ranges must be requested again rather than skipped over a store that was just emptied.
    [Test]
    public void Will_request_account_ranges_when_range_phase_not_finished()
    {
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.IsRangePhaseFinished().Returns(false);
        using ProgressTracker progressTracker = CreateProgressTracker(snapTrieFactory: snapTrieFactory);

        progressTracker.LoadProgress();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? request), Is.False);
        Assert.That(request!.AccountRangeRequest, Is.Not.Null);
        request.Dispose();
    }

    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0x2000000000000000000000000000000000000000000000000000000000000000", null, "0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [TestCase("0x2000000000000000000000000000000000000000000000000000000000000000", "0x4000000000000000000000000000000000000000000000000000000000000000", "0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0x67ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [TestCase("0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xbfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", null, "0xdfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void Should_partition_storage_request_if_last_processed_less_than_threshold(string start, string lastProcessed, string? limit, string expectedSplit)
    {
        using ProgressTracker progressTracker = CreateProgressTracker(enableStorageSplits: true);

        ValueHash256 lastProcessedHash = new(lastProcessed);
        ValueHash256? limitHash = limit is null ? (ValueHash256?)null : new ValueHash256(limit);

        StorageRange storageRange = new()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { TestItem.Tree.AccountsWithPaths[0] },
            StartingHash = new ValueHash256(start),
            LimitHash = limitHash
        };
        progressTracker.EnqueueNextSlot(storageRange, 0, lastProcessedHash, 1_000_000_000);

        //ignore account range
        bool isFinished = progressTracker.IsFinished(out _);

        //expecting 2 batches
        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch1);
        Assert.That(isFinished, Is.False);
        Assert.That(batch1, Is.Not.Null);

        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch2);
        Assert.That(isFinished, Is.False);
        Assert.That(batch2, Is.Not.Null);

        Assert.That(batch2?.StorageRangeRequest?.StartingHash, Is.EqualTo(batch1?.StorageRangeRequest?.LimitHash?.IncrementPath()));
        Assert.That(batch1?.StorageRangeRequest?.StartingHash, Is.EqualTo(lastProcessedHash.IncrementPath()));
        Assert.That(batch2?.StorageRangeRequest?.LimitHash, Is.EqualTo(limitHash ?? Keccak.MaxValue));

        Assert.That(batch1?.StorageRangeRequest?.LimitHash, Is.EqualTo(new ValueHash256(expectedSplit)));
    }


    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000000", "0xb100000000000000000000000000000000000000000000000000000000000000", null)]
    [TestCase("0x8fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xdfffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", null)]
    public void Should_not_partition_storage_request_if_last_processed_more_than_threshold(string start, string lastProcessed, string? limit)
    {
        using ProgressTracker progressTracker = CreateProgressTracker();

        ValueHash256 lastProcessedHash = new(lastProcessed);
        ValueHash256? limitHash = limit is null ? (ValueHash256?)null : new ValueHash256(limit);

        StorageRange storageRange = new()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { TestItem.Tree.AccountsWithPaths[0] },
            StartingHash = new ValueHash256(start),
            LimitHash = limitHash
        };
        progressTracker.EnqueueNextSlot(storageRange, 0, lastProcessedHash, 100000000);

        //ignore account range
        bool isFinished = progressTracker.IsFinished(out _);

        //expecting 1 batch
        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch1);
        Assert.That(isFinished, Is.False);
        Assert.That(batch1, Is.Not.Null);

        Assert.That(batch1?.StorageRangeRequest?.StartingHash, Is.EqualTo(lastProcessedHash.IncrementPath()));
        Assert.That(batch1?.StorageRangeRequest?.LimitHash, Is.EqualTo(limitHash ?? Keccak.MaxValue));
    }

    // Regression for #13200. SnapSyncFeed.AnalyzeResponsePerPeer reaches the pivot only through UpdatePivot, and a
    // forced move costs every in-flight and queued range its root. On a chain whose head moves between two failure
    // streaks an unguarded move chases the head forever and the invalidated ranges feed the next streak.
    [TestCase(0UL, false, TestName = "Head level with the pivot")]
    [TestCase(1UL, false, TestName = "Head one block ahead - not worth invalidating in-flight ranges")]
    [TestCase(2UL, false, TestName = "Head two blocks ahead - one OP Mainnet block time")]
    [TestCase(31UL, false, TestName = "One block below the minimum step")]
    [TestCase(32UL, true, TestName = "Exactly the minimum step")]
    [TestCase(500UL, true, TestName = "Far behind - always worth moving")]
    public void UpdatePivot_only_moves_the_pivot_once_the_head_is_a_minimum_step_ahead(ulong diff, bool shouldMove)
    {
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        pivot.Diff.Returns(diff);
        SyncConfig syncConfig = new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1, StateMinDistanceFromHead = 32UL };
        using ProgressTracker progressTracker = new(Substitute.For<ISnapTrieFactory>(), syncConfig, pivot, LimboLogs.Instance);

        progressTracker.UpdatePivot();

        pivot.Received(shouldMove ? 1 : 0).UpdateHeaderForcefully();
    }

    // A refresh answered with an expired root re-queues itself, so an unconditional priority for that queue hands it
    // every dispatcher slot while it cannot drain. Code requests are keyed by hash and succeed against a peer behind
    // the pivot, so they must still get served. The bound is on refreshes served in a row rather than on refreshes in
    // flight, which is why the two cases below - all four in flight, and never more than one in flight - behave the
    // same: a dispatcher running fewer workers than the cap would never reach an in-flight bound at all.
    [TestCase(false, TestName = "Refreshes yield after their turn (nothing reported finished, all in flight)")]
    [TestCase(true, TestName = "Refreshes yield after their turn at one worker (each reported finished first)")]
    public void Will_not_let_account_refreshes_take_every_dispatcher_slot(bool oneWorker)
    {
        using ProgressTracker progressTracker = CreateProgressTracker();
        DrainAccountRangePartition(progressTracker);

        for (int i = 0; i < ProgressTracker.MAX_CONSECUTIVE_ACCOUNT_REFRESHES + 4; i++)
        {
            progressTracker.EnqueueAccountRefresh(new PathWithAccount { Path = TestItem.ValueKeccaks[i] }, null, null);
        }

        progressTracker.EnqueueCodeHashes([TestItem.ValueKeccaks[0]]);

        for (int i = 0; i < ProgressTracker.MAX_CONSECUTIVE_ACCOUNT_REFRESHES; i++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? refresh);
            using (refresh)
            {
                Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null, $"request {i} is within the turn");
            }

            // One worker: the request is finished before the next scheduling decision, so nothing is ever in flight.
            if (oneWorker) progressTracker.ReportAccountRefreshFinished();
        }

        progressTracker.IsFinished(out SnapSyncBatch? request);
        using (request)
        {
            Assert.That(request!.AccountsToRefreshRequest, Is.Null, "the turn is over, so the refresh queue must yield");
            Assert.That(request.CodesRequest, Is.Not.Null);
        }
    }

    [Test]
    public void Will_still_serve_account_refreshes_over_the_cap_when_nothing_else_is_queued()
    {
        using ProgressTracker progressTracker = CreateProgressTracker();
        DrainAccountRangePartition(progressTracker);

        for (int i = 0; i < ProgressTracker.MAX_CONSECUTIVE_ACCOUNT_REFRESHES + 1; i++)
        {
            progressTracker.EnqueueAccountRefresh(new PathWithAccount { Path = TestItem.ValueKeccaks[i] }, null, null);
        }

        for (int i = 0; i <= ProgressTracker.MAX_CONSECUTIVE_ACCOUNT_REFRESHES; i++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? refresh);
            using (refresh)
            {
                Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null,
                    $"request {i}: with nothing else queued the tail of the sync must not stall on the turn limit");
            }
        }
    }

    /// <summary>Takes the one account-range partition out of the way so the priority chain reaches the queues under test.</summary>
    private static void DrainAccountRangePartition(ProgressTracker progressTracker)
    {
        progressTracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        {
            Assert.That(batch!.AccountRangeRequest, Is.Not.Null);
        }
    }

    /// <param name="snapResume">Turns checkpoints on, by default taken whenever no request is in flight.</param>
    private ProgressTracker CreateProgressTracker(int accountRangePartition = 1, bool enableStorageSplits = false, ISnapTrieFactory? snapTrieFactory = null, ILogManager? logManager = null, bool snapResume = false, IStateSyncPivot? pivot = null, TimeSpan? checkpointInterval = null)
    {
        BlockTree blockTree = Build.A.BlockTree().WithStateRoot(Keccak.EmptyTreeHash).OfChainLength(2).TestObject;
        SyncConfig syncConfig = new TestSyncConfig() { SnapSyncAccountRangePartitionCount = accountRangePartition, EnableSnapSyncStorageRangeSplit = enableStorageSplits, SnapResume = snapResume };
        return new(snapTrieFactory ?? Substitute.For<ISnapTrieFactory>(), syncConfig, pivot ?? new StateSyncPivot(blockTree, syncConfig, LimboLogs.Instance), logManager ?? LimboLogs.Instance, checkpointInterval ?? TimeSpan.Zero);
    }
}
