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
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

using Autofac;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Stats.Model;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;


using System.Collections;

namespace Nethermind.Synchronization.Test.SnapSync;

public class ProgressTrackerTests
{
    [Test]
    public void Response_admission_charges_active_promises_before_release_and_never_promotes_refused_retries(
        [Values(64, 65, 1200)] int count)
    {
        using StorageRange source = CreateRequest(count);
        using MemDb db = new();
        TestRawTrieStore store = new(db);
        StateTree state = new(store, LimboLogs.Instance);
        using (store.BeginBlockCommit(1))
        {
            foreach (PathWithAccount account in source.Accounts) state.Set(account.Path, account.Account.ToAccount());
            state.Commit();
        }
        using IContainer container = CreateContainer(root: state.RootHash);
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        foreach (PathWithAccount account in source.Accounts)
            tracker.RetryAccountRefresh(new AccountWithStorageStartingHash { PathAndAccount = account, StorageHashLimit = ValueKeccak.MaxValue }, storage: true);
        Assert.That(provider.AddAccountRange(1, state.RootHash, ValueKeccak.Zero, source.Accounts), Is.EqualTo(AddRangeResult.OK));
        tracker.IsFinished(out SnapSyncBatch? active);
        Assert.That(active!.StorageRangeRequest!.Accounts.Count, Is.EqualTo(count));
        ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> lists = [with(count)];
        for (int i = 0; i < count; i++) lists.Add(new ArrayPoolList<PathWithStorageSlot>(0));
        using (SlotsAndProofs response = new() { PathsAndSlots = lists })
            Assert.That(provider.AddStorageRange(active.StorageRangeRequest, response), Is.EqualTo(AddRangeResult.EmptyRange));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.AccountsToRefreshCount, Is.Zero, "active retry ownership keeps paid promotions latent");
            Assert.That(tracker.Recovery.CohortSlots, Is.EqualTo(64));
        }
        ProgressTracker.ResponseAdmission duplicate = new(tracker.Recovery, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.TryEnqueueAccountRefresh(source.Accounts[0], null, null, duplicate), Is.True);
            Assert.That(tracker.Recovery.CohortSlots, Is.EqualTo(64), "coalescing the same paid promise consumes no new slot");
        }
        if (count > 64)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracker.TryEnqueueAccountRefresh(source.Accounts[64], null, null, duplicate), Is.False,
                    "a fresh response admission cannot pay another promise while the global cohort is full");
                Assert.That(tracker.Recovery.CohortSlots, Is.EqualTo(64));
            }
        }
        provider.ReleaseRequest(active, true);
        active.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(64));
            Assert.That(tracker.Recovery.CohortSlots, Is.EqualTo(64));
        }
        tracker.IsFinished(out SnapSyncBatch? retry);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retry!.AccountsToRefreshRequest, Is.Null, "original-root account proof completes paid refresh ownership locally");
            Assert.That(retry.StorageRangeRequest!.Accounts.Count, Is.EqualTo(count), "refused accounts remain ordinary storage work");
        }
        provider.ReleaseRequest(retry, true);
        retry.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
            Assert.That(tracker.Recovery.CohortSlots, Is.Zero);
            Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
        }
    }

    [Test]
    public void SaveCheckpoint_RecordsTheAppliedRangePivotAndSkipsTheTimedOutPivot()
    {
        ISnapTrieFactory factory = Substitute.For<ISnapTrieFactory>();
        factory.SupportsCheckpoints.Returns(true);
        BlockHeader applied = Build.A.BlockHeader.WithNumber(11).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        BlockHeader timedOut = Build.A.BlockHeader.WithNumber(10).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        pivot.GetPivotHeader().Returns(applied);
        pivot.UpdatedStorages.Returns([]);
        using ProgressTracker tracker = CreateProgressTracker(accountRangePartition: 2, snapTrieFactory: factory,
            snapResume: true, pivot: pivot, checkpointInterval: TimeSpan.MaxValue);

        tracker.IsFinished(out SnapSyncBatch? appliedRequest);
        using (appliedRequest)
        {
            ValueHash256 limit = appliedRequest!.AccountRangeRequest!.LimitHash!.Value;
            tracker.UpdateAccountRangePartitionProgress(limit, limit, moreChildrenToRight: false);
            tracker.ReportAccountRangePartitionFinished(limit);
            tracker.ReportRequestReleased(appliedRequest, madeRangeProgress: true);
        }
        pivot.GetPivotHeader().Returns(timedOut);
        tracker.IsFinished(out SnapSyncBatch? timedOutRequest);
        using (timedOutRequest)
        {
            tracker.ReportRequestReleased(timedOutRequest!);
        }

        tracker.SaveCheckpointAtShutdown();

        (ulong Number, Hash256 Hash)[] expectedPivots = [(applied.Number, applied.Hash!)];
        factory.Received(1).SaveCheckpoint(Arg.Is<SnapCheckpoint>(checkpoint =>
            checkpoint.AnchorNumber == applied.Number && checkpoint.AnchorHash == applied.Hash
            && checkpoint.RangePivots.SequenceEqual(expectedPivots)));
    }

    [Test]
    [Repeat(3)]
    public async Task Did_not_have_race_issue([Values] bool zeroStart)
    {
        using ProgressTracker progressTracker = CreateProgressTracker(accountRangePartition: 1);
        progressTracker.IsFinished(out SnapSyncBatch? accountBatch);
        using (accountBatch)
        {
            progressTracker.UpdateAccountRangePartitionProgress(ValueKeccak.MaxValue, ValueKeccak.MaxValue, false);
            progressTracker.ReportAccountRangePartitionFinished(ValueKeccak.MaxValue);
        }
        progressTracker.EnqueueNextSlot(new StorageRange()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.KeccakA, new(0, 0)) },
            StartingHash = zeroStart ? ValueKeccak.Zero : ValueKeccak.Zero.IncrementPath()
        });

        int loopIteration = 100000;
        Task requestTask = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < loopIteration; i++)
            {
                bool finished = progressTracker.IsFinished(out SnapSyncBatch? snapSyncBatch);
                Assert.That(finished, Is.False);
                progressTracker.ReleasePreparedCohort(snapSyncBatch!.StorageRangeRequest!);
                progressTracker.EnqueueNextSlot(snapSyncBatch.StorageRangeRequest!);
                progressTracker.ReportStorageRequestFinished(1);
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
#pragma warning disable NUnit2045 // The nullable value must be checked before accessing its contents.
            Assert.That(request!.AccountRangeRequest, Is.Not.Null);
#pragma warning restore NUnit2045
            Assert.That(request.AccountRangeRequest!.StartingHash, Is.EqualTo(expectedStarts[i]));
            Assert.That(request.AccountRangeRequest.LimitHash!.Value, Is.EqualTo(expectedLimits[i]));
            Assert.That(finished, Is.False);
            request.Dispose();
        }

        bool finalFinished = progressTracker.IsFinished(out SnapSyncBatch? finalRequest);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finalRequest, Is.Null);
            Assert.That(finalFinished, Is.False);
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request!.CodesRequest, Is.Not.Null);
            Assert.That(request.StorageRangeRequest, Is.Null);
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request!.CodesRequest, Is.Null);
            Assert.That(request.StorageRangeRequest, Is.Not.Null);
        }
        request.Dispose();
    }

    // Healing resumes after a graceful stop from the checkpoint of the finished ranges, which keeps the anchor and the
    // accounts to heal. The last periodic checkpoint is deleted first, so a failed save leaves none that still has
    // ranges to download. A backend that cannot resume is never checkpointed at all.
    [Test]
    [NonParallelizable]
    public void Will_mark_range_phase_finished_when_ranges_drain([Values] bool snapResume, [Values] bool supportsCheckpoints)
    {
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.SupportsCheckpoints.Returns(supportsCheckpoints);
        List<SnapCheckpoint?> saved = [];
        snapTrieFactory.When(static f => f.SaveCheckpoint(Arg.Any<SnapCheckpoint?>())).Do(call => saved.Add(call.Arg<SnapCheckpoint?>()));
        bool checkpointed = snapResume && supportsCheckpoints;
        BlockHeader pivotHeader = Build.A.BlockHeader.WithNumber(5).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        pivot.GetPivotHeader().Returns(pivotHeader);
        pivot.UpdatedStorages.Returns([]);
        TestLogger logger = new();
        using ProgressTracker progressTracker = CreateProgressTracker(snapTrieFactory: snapTrieFactory, logManager: new OneLoggerLogManager(new ILogger(logger)), snapResume: snapResume, pivot: pivot);

        progressTracker.TrackAccountToHeal(TestItem.ValueKeccaks[0]);
        progressTracker.IsFinished(out SnapSyncBatch? request);
        Assert.That(request!.AccountRangeRequest, Is.Not.Null);
        progressTracker.UpdateAccountRangePartitionProgress(request.AccountRangeRequest!.LimitHash!.Value, Keccak.MaxValue, false);
        progressTracker.ReportAccountRangePartitionFinished(request.AccountRangeRequest!.LimitHash!.Value);
        progressTracker.ReportRequestReleased(request, madeRangeProgress: true);
        request.Dispose();
        Assert.That(Metrics.SnapRangesProgress, Is.EqualTo(ProgressTracker.RangesProgressScale - 1));

        bool finished = progressTracker.IsFinished(out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finished, Is.True);
            Assert.That(Metrics.SnapRangesProgress, Is.EqualTo(ProgressTracker.RangesProgressScale));
        }

        snapTrieFactory.Received(1).MarkRangePhaseFinished();
        using (Assert.EnterMultipleScope())
        {
            if (checkpointed)
            {
                Assert.That(saved[^2], Is.Null, "the last periodic checkpoint is deleted first");
                Assert.That(saved[^1]?.Partitions.Select(static p => p.MoreAccountsToRight), Is.EqualTo(new[] { false }), "finished partitions");
                Assert.That(saved[^1]?.StorageRangesToRefetch(), Is.Empty, "storage left to download");
                Assert.That(saved[^1]?.CodesToRetrieve, Is.Empty, "codes left to download");
                Assert.That(saved[^1]?.UpdatedStorages, Is.EqualTo(new[] { TestItem.ValueKeccaks[0].ToCommitment() }), "accounts to heal");
                Assert.That((saved[^1]?.AnchorNumber, saved[^1]?.AnchorHash), Is.EqualTo((pivotHeader.Number, pivotHeader.Hash)), "anchor");
                Assert.That(saved[^1]?.RangePivots, Is.EqualTo(new[] { (pivotHeader.Number, pivotHeader.Hash) }));
            }
            else
            {
                Assert.That(saved, Is.Empty);
            }

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

        PathWithAccount withStorage = new(TestItem.ValueKeccaks[1], Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject.ToStruct());
        PathWithAccount toRefresh = new(TestItem.ValueKeccaks[2], Build.An.Account.WithBalance(2).TestObject.ToStruct());
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

            interrupted.EnqueueAccountStorage(new PathWithAccount(TestItem.ValueKeccaks[1], Build.An.Account.WithStorageRoot(TestItem.KeccakA).TestObject.ToStruct()));
            interrupted.EnqueueAccountStorage(new PathWithAccount(TestItem.ValueKeccaks[2], Build.An.Account.WithStorageRoot(TestItem.KeccakB).TestObject.ToStruct()));
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.IsFinished(out SnapSyncBatch? next), Is.True, "the answered storage was the last request, so the phase is done");
            Assert.That(next, Is.Null);
        }
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

    [Test]
    public void Checkpoint_rejects_an_account_entry_carrying_no_account()
    {
        AccountStruct account = new(1);
        byte[] encoded = new SnapCheckpoint
        {
            AnchorNumber = 1,
            AnchorHash = TestItem.KeccakA,
            Partitions = [],
            StoragesToRetrieve = [new PathWithAccount(TestItem.ValueKeccaks[0], account)],
            StorageRanges = [],
            AccountsToRefresh = [],
            CodesToRetrieve = [],
            UpdatedStorages = [],
        }.Encode();
        byte[] accountRlp = AccountDecoder.Instance.Encode(account);
        int rlpStart = encoded.AsSpan().IndexOf(accountRlp);
        // The entry's 4-byte length and account RLP become a one-byte placeholder that carries no account.
        byte[] carryingNoAccount = [.. encoded[..(rlpStart - sizeof(int))], .. Bytes.FromHexString("00000001c1"), .. encoded[(rlpStart + accountRlp.Length)..]];

        Assert.That(() => SnapCheckpoint.Decode(carryingNoAccount), Throws.TypeOf<InvalidDataException>());
    }

    // Regression: a second range for an account already being refreshed waits in that refresh's registry entry, and a
    // checkpoint kept only the queued range, so a second shutdown dropped the waiting one. A refresh deferred past the
    // refresh cap was saved as a plain storage range, so it came back without its refresh.
    [Test]
    public void Refreshes_waiting_or_deferred_survive_a_restore_and_a_second_checkpoint()
    {
        SnapCheckpoint? saved = null;
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.SupportsCheckpoints.Returns(true);
        snapTrieFactory.When(static f => f.SaveCheckpoint(Arg.Any<SnapCheckpoint?>())).Do(call => saved = call.Arg<SnapCheckpoint?>());
        snapTrieFactory.LoadCheckpoint().Returns(_ => saved is null ? null : SnapCheckpoint.Decode(saved.Encode()));

        PathWithAccount account = new(TestItem.ValueKeccaks[2], Build.An.Account.WithBalance(2).WithStorageRoot(TestItem.KeccakA).TestObject.ToStruct());
        PathWithAccount deferred = new(TestItem.ValueKeccaks[3], Build.An.Account.WithBalance(3).WithStorageRoot(TestItem.KeccakA).TestObject.ToStruct());
        ValueHash256 firstFrom = new("0x4000000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 secondFrom = new("0x8000000000000000000000000000000000000000000000000000000000000000");
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        pivot.GetPivotHeader().Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(Keccak.EmptyTreeHash).TestObject);
        pivot.UpdatedStorages.Returns([]);

        using (ProgressTracker interrupted = CreateProgressTracker(snapTrieFactory: snapTrieFactory, snapResume: true, pivot: pivot, checkpointInterval: TimeSpan.MaxValue))
        {
            interrupted.IsFinished(out SnapSyncBatch? accountRange);
            accountRange!.Dispose();
            interrupted.EnqueueAccountRefresh(account, firstFrom, null);
            interrupted.EnqueueAccountRefresh(account, secondFrom, null);
            for (int i = 1; i < SnapProvider.MaxQueuedEmptyStreakRefreshes; i++)
                interrupted.EnqueueAccountRefresh(Filler(i), null, null);
            interrupted.EnqueueAccountRefresh(deferred, firstFrom, null);
            interrupted.SaveCheckpointAtShutdown();
        }

        using (ProgressTracker restarted = CreateProgressTracker(snapTrieFactory: snapTrieFactory, snapResume: true, pivot: pivot, checkpointInterval: TimeSpan.MaxValue))
        {
            restarted.Reset();
            restarted.LoadProgress();
            restarted.SaveCheckpointAtShutdown();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved!.AccountsToRefresh.Where(r => r.PathAndAccount?.Path == account.Path).Select(static r => r.StorageStartingHash),
                Is.EquivalentTo(new[] { firstFrom, secondFrom }));
            Assert.That(saved.AccountsToRefresh.Where(r => r.PathAndAccount?.Path == deferred.Path).Select(static r => r.StorageStartingHash),
                Is.EqualTo(new[] { firstFrom }), "the refresh deferred past the cap stays a refresh");
            Assert.That(saved.StorageRanges.SelectMany(static r => r.Accounts).Select(static a => a.Path), Has.No.Member(deferred.Path));
            Assert.That(saved.AccountsToRefresh, Has.Length.EqualTo(SnapProvider.MaxQueuedEmptyStreakRefreshes + 2));
        }

        using ProgressTracker resumed = CreateProgressTracker(snapTrieFactory: snapTrieFactory, snapResume: true, pivot: pivot, checkpointInterval: TimeSpan.MaxValue);
        resumed.Reset();
        resumed.LoadProgress();

        List<SnapSyncBatch> batches = [];
        while (batches.Count < 100 && !resumed.IsFinished(out SnapSyncBatch? batch) && batch is not null) batches.Add(batch);
        AccountsToRefreshRequest refresh = batches
            .Select(static b => b.AccountsToRefreshRequest)
            .Single(r => r is not null && r.Paths.AsSpan().ToArray().Any(p => p.PathAndAccount?.Path == account.Path))!;
        resumed.RecordRefreshedAccount(refresh.Paths.AsSpan().ToArray().First(p => p.PathAndAccount?.Path == account.Path), account.Account, Keccak.EmptyTreeHash);
        resumed.ReportAccountRefreshFinished(refresh, responseHandled: true);
        while (batches.Count < 200 && !resumed.IsFinished(out SnapSyncBatch? batch) && batch is not null) batches.Add(batch);

        Assert.That(batches
                .Where(b => b.StorageRangeRequest is { } r && r.Accounts.AsSpan().ToArray().Any(a => a.Path == account.Path))
                .Select(static b => b.StorageRangeRequest!.StartingHash),
            Is.EquivalentTo(new ValueHash256?[] { firstFrom, secondFrom }));
        foreach (SnapSyncBatch batch in batches) batch.Dispose();
    }

    private static PathWithAccount Filler(int i) =>
        new(Keccak.Compute(i.ToString()).ValueHash256, Build.An.Account.WithBalance((ulong)i).WithStorageRoot(TestItem.KeccakA).TestObject.ToStruct());

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
        PathWithAccount withStorage = new(TestItem.ValueKeccaks[1], Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakA).TestObject.ToStruct());
        PathWithAccount toRefresh = new(TestItem.ValueKeccaks[2], Build.An.Account.WithBalance(2).TestObject.ToStruct());
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
            Assert.That(batches.Single(static b => b.StorageRangeRequest is not null).StorageRangeRequest!.Accounts[0].Account.StorageRoot, Is.EqualTo(TestItem.KeccakA));
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

#pragma warning disable NUnit2045 // Confirm the wait outcome before checking checkpoint state.
            Assert.That(next.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "the checkpoint is due, so it waits for the response");
#pragma warning restore NUnit2045
            Assert.That(Volatile.Read(ref checkpointsSaved), Is.EqualTo(1));
        }

#pragma warning disable NUnit2045 // Confirm the wait outcome before checking checkpoint state.
        Assert.That(next.Wait(TimeSpan.FromSeconds(10)), Is.True);
#pragma warning restore NUnit2045
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progressTracker.IsFinished(out SnapSyncBatch? request), Is.True);
            Assert.That(request, Is.Null);
            Assert.That(logger.LogList, Has.Some.EqualTo("Snap - State Ranges (Phase 1 of 2) is finished."));
        }
    }

    // Regression: account ranges must be requested again rather than skipped over a store that was just emptied.
    [Test]
    public void Will_request_account_ranges_when_range_phase_not_finished()
    {
        ISnapTrieFactory snapTrieFactory = Substitute.For<ISnapTrieFactory>();
        snapTrieFactory.IsRangePhaseFinished().Returns(false);
        using ProgressTracker progressTracker = CreateProgressTracker(snapTrieFactory: snapTrieFactory);

        progressTracker.LoadProgress();

#pragma warning disable NUnit2045 // Keep prerequisite acquisition outside a grouped scope; its output is used by later assertions or subsequent setup/disposal.
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? request), Is.False);
#pragma warning restore NUnit2045
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(isFinished, Is.False);
            Assert.That(batch1, Is.Not.Null);
        }

        progressTracker.ReleasePreparedCohort(batch1!.StorageRangeRequest!);
        isFinished = progressTracker.IsFinished(out SnapSyncBatch? batch2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(isFinished, Is.False);
            Assert.That(batch2, Is.Not.Null);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch2?.StorageRangeRequest?.StartingHash, Is.EqualTo(batch1?.StorageRangeRequest?.LimitHash?.IncrementPath()));
            Assert.That(batch1?.StorageRangeRequest?.StartingHash, Is.EqualTo(lastProcessedHash.IncrementPath()));
            Assert.That(batch2?.StorageRangeRequest?.LimitHash, Is.EqualTo(limitHash ?? Keccak.MaxValue));

            Assert.That(batch1?.StorageRangeRequest?.LimitHash, Is.EqualTo(new ValueHash256(expectedSplit)));
        }
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(isFinished, Is.False);
            Assert.That(batch1, Is.Not.Null);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch1?.StorageRangeRequest?.StartingHash, Is.EqualTo(lastProcessedHash.IncrementPath()));
            Assert.That(batch1?.StorageRangeRequest?.LimitHash, Is.EqualTo(limitHash ?? Keccak.MaxValue));
        }
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
            using (Assert.EnterMultipleScope())
            {
                Assert.That(request!.AccountsToRefreshRequest, Is.Null, "the turn is over, so the refresh queue must yield");
                Assert.That(request.CodesRequest, Is.Not.Null);
            }
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

    [Test]
    public void Active_refresh_cannot_be_queued_again()
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        using StorageRange request = CreateRequest(1);
        tracker.EnqueueAccountRefresh(request.Accounts[0], null, null);
        tracker.IsFinished(out SnapSyncBatch? active);
        using (active)
        {
            Assert.That(active!.AccountsToRefreshRequest, Is.Not.Null);
            tracker.EnqueueAccountRefresh(request.Accounts[0], null, null);
            Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
            container.Resolve<SnapProvider>().ReleaseRequest(active, true);
        }
    }

    [Test]
    public void Overflow_storage_retry_keeps_ownership_after_refresh_capacity_frees()
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        using StorageRange request = CreateRequest(65);
        foreach (PathWithAccount account in request.Accounts.AsSpan()) tracker.EnqueueAccountRefresh(account, null, null);
        tracker.IsFinished(out SnapSyncBatch? active);
        using (active) container.Resolve<SnapProvider>().ReleaseRequest(active!, true);
        tracker.EnqueueAccountRefresh(request.Accounts[64], null, null);
        Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(63));
    }

    [Test]
    public void Refresh_retry_waits_for_release_and_preserves_its_range([Values] bool storage)
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        using StorageRange request = CreateRequest(1);
        ValueHash256 limit = ValueKeccak.MaxValue.DecrementPath();
        tracker.EnqueueAccountRefresh(request.Accounts[0], ValueKeccak.Zero, limit);
        tracker.IsFinished(out SnapSyncBatch? active);
        using (active)
        {
            tracker.RetryAccountRefresh(active!.AccountsToRefreshRequest!.Paths[0], storage);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracker.IsFinished(out SnapSyncBatch? waiting), Is.False);
                Assert.That(waiting, Is.Null, "the active range must not be allocated twice");
            }
            provider.ReleaseRequest(active, true);
        }
        tracker.IsFinished(out SnapSyncBatch? retry);
        using (retry)
        {
            if (storage)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(retry!.StorageRangeRequest!.StartingHash, Is.EqualTo(ValueKeccak.Zero));
                    Assert.That(retry.StorageRangeRequest.LimitHash, Is.EqualTo(limit));
                }
                tracker.OnCompletedLargeStorage(request.Accounts[0]);
            }
            else
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(retry!.AccountsToRefreshRequest!.Paths[0].StorageStartingHash, Is.EqualTo(ValueKeccak.Zero));
                    Assert.That(retry.AccountsToRefreshRequest.Paths[0].StorageHashLimit, Is.EqualTo(limit));
                }
            }
            provider.ReleaseRequest(retry, true);
        }
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
    }

    [Test]
    public void Active_storage_fallback_transfers_ownership_only_at_release([Values] bool responseHandled, [Values] bool emptyResponse)
    {
        using StorageRange request = CreateRequest(65);
        using MemDb serverDb = new();
        TestRawTrieStore store = new(serverDb);
        StateTree source = new(store, LimboLogs.Instance);
        using (store.BeginBlockCommit(1))
        {
            foreach (PathWithAccount account in request.Accounts) source.Set(account.Path, account.Account.ToAccount());
            source.Commit();
        }
        SnapStateServer server = new(store.AsReadOnly(), LimboLogs.Instance);
        using IContainer container = CreateContainer(root: source.RootHash);
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        for (int i = 0; i < 64; i++) tracker.EnqueueAccountRefresh(request.Accounts[i], null, null);
        tracker.EnqueueAccountRefresh(request.Accounts[64], null, null);
        tracker.EnqueueAccountRefresh(request.Accounts[64], null, null);
        ValueHash256 start = request.Accounts[64].Path;
        (IOwnedReadOnlyList<PathWithAccount> values, IByteArrayList proofs) =
            server.GetAccountRanges(source.RootHash, start, start.IncrementPath(), 4000, CancellationToken.None);
        using AccountsAndProofs verified = new() { PathAndAccounts = values, Proofs = proofs };
        Assert.That(provider.AddAccountRange(1, source.RootHash, start, [request.Accounts[64]], verified.Proofs), Is.EqualTo(AddRangeResult.OK));
        List<SnapSyncBatch> held = [];
        SnapSyncBatch? storage = null;
        while (storage is null)
        {
            tracker.IsFinished(out SnapSyncBatch? next);
            Assert.That(next, Is.Not.Null);
            if (next!.StorageRangeRequest is not null) storage = next;
            else held.Add(next);
        }
        foreach (SnapSyncBatch refresh in held)
        {
            provider.ReleaseRequest(refresh, true);
            refresh.Dispose();
        }
        int queued = tracker.AccountsToRefreshCount;
        tracker.EnqueueAccountRefresh(request.Accounts[64], null, null);
        Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(queued));
        if (emptyResponse)
        {
            using SlotsAndProofs response = new();
            Assert.That(provider.AddStorageRange(storage.StorageRangeRequest!, response), Is.EqualTo(AddRangeResult.ExpiredRootHash));
        }
        provider.ReleaseRequest(storage, responseHandled);
        storage.Dispose();
        Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(queued + (responseHandled ? 1 : 0)));
        HashSet<ValueHash256> remaining = [];
        while (!tracker.IsFinished(out SnapSyncBatch? next))
        {
            using (next)
            {
                Assert.That(next, Is.Not.Null);
                PathWithAccount account = next!.AccountsToRefreshRequest?.Paths[0].PathAndAccount ?? next.StorageRangeRequest!.Accounts[0];
                Assert.That(remaining.Add(account.Path), Is.True);
                tracker.DropLargeStorageProgress(account);
                provider.ReleaseRequest(next, true);
            }
        }
        Assert.That(remaining.Contains(request.Accounts[64].Path), Is.True);
    }

    [Test]
    public void Unhandled_multi_account_request_preserves_first_range_bounds([Values] bool zeroStart)
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        using StorageRange request = CreateRequest(2);
        request.StartingHash = zeroStart ? ValueKeccak.Zero : ValueKeccak.Zero.IncrementPath();
        request.LimitHash = ValueKeccak.MaxValue.DecrementPath();
        tracker.RequeueStorageRange(request.Copy());
        tracker.IsFinished(out SnapSyncBatch? first);
        using (first)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(first!.StorageRangeRequest!.Accounts[0], Is.SameAs(request.Accounts[0]));
                Assert.That(first.StorageRangeRequest.StartingHash, Is.EqualTo(request.StartingHash));
                Assert.That(first.StorageRangeRequest.LimitHash, Is.EqualTo(request.LimitHash));
            }
            tracker.OnCompletedLargeStorage(request.Accounts[0]);
            provider.ReleaseRequest(first, true);
        }
        tracker.IsFinished(out SnapSyncBatch? second);
        using (second)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(second!.StorageRangeRequest!.Accounts[0], Is.SameAs(request.Accounts[1]));
                Assert.That(second.StorageRangeRequest.StartingHash.GetValueOrDefault(), Is.EqualTo(ValueKeccak.Zero));
            }
            provider.ReleaseRequest(second, true);
        }
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
    }

    [Test]
    public void Unhandled_zero_origin_full_ranges_return_to_one_storage_batch()
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        using StorageRange accounts = CreateRequest(3);
        foreach (PathWithAccount account in accounts.Accounts)
        {
            tracker.EnqueueNextSlot(new StorageRange
            {
                Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                StartingHash = ValueKeccak.Zero.IncrementPath(),
                LimitHash = ValueKeccak.MaxValue
            });
        }
        for (int i = 0; i < accounts.Accounts.Count; i++)
        {
            tracker.IsFinished(out SnapSyncBatch? request);
            using (request)
            {
                Assert.That(request!.StorageRangeRequest!.Accounts, Has.Count.EqualTo(1));
                request.StorageRangeRequest.StartingHash = ValueKeccak.Zero;
                provider.ReleaseRequest(request, responseHandled: false);
            }
        }
        tracker.IsFinished(out SnapSyncBatch? retry);
        using (retry)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry!.StorageRangeRequest!.Accounts, Is.EquivalentTo(accounts.Accounts));
                Assert.That(retry.StorageRangeRequest.StartingHash.GetValueOrDefault(), Is.EqualTo(ValueKeccak.Zero));
                Assert.That(retry.StorageRangeRequest.LimitHash ?? ValueKeccak.MaxValue, Is.EqualTo(ValueKeccak.MaxValue));
            }
            provider.ReleaseRequest(retry, responseHandled: true);
        }
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
    }

    [Test, CancelAfter(30_000)]
    public void Refresh_completion_reads_the_pivot_once_without_holding_refresh_ownership(
        [Values(0, 1, 2)] int completionKind)
    {
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        BlockHeader header = Build.A.BlockHeader.WithStateRoot(TestItem.KeccakA).TestObject;
        pivot.GetPivotHeader().Returns(header);
        ConcurrentCollections.ConcurrentHashSet<Hash256> updatedStorages = [];
        pivot.UpdatedStorages.Returns(updatedStorages);
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
            .WithSuggestedHeaderOfStateRoot(TestItem.KeccakA)
            .AddSingleton(pivot).Build();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        using StorageRange accounts = CreateRequest(1);
        PathWithAccount first = accounts.Accounts[0];
        for (int i = 0; i < 3; i++)
            tracker.EnqueueAccountRefresh(i == 0 ? first : new(first.Path, first.Account), ValueKeccak.Zero, ValueKeccak.MaxValue);
        SnapSyncBatch? request = null;
        if (completionKind != 2)
        {
            tracker.IsFinished(out request);
            tracker.RecordRefreshedAccount(request!.AccountsToRefreshRequest!.Paths[0], Account.TotallyEmpty.ToStruct(), header.StateRoot);
        }
        else tracker.SetAccountEvidence(_ => (AccountStruct.TotallyEmpty, header.StateRoot!.ValueHash256), static (_, _, _) => { });
        int reads = 0;
        pivot.GetPivotHeader().Returns(_ =>
        {
            reads++;
            Task<bool> ownershipProbe = Task.Run(() => tracker.TryEnqueueAccountRefresh(first, ValueKeccak.Zero, ValueKeccak.MaxValue));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ownershipProbe.Wait(TimeSpan.FromSeconds(5)), Is.True,
                    "external pivot access must allow another thread to acquire refresh ownership");
                Assert.That(ownershipProbe.Result, Is.True);
            }
            return header;
        });
        using (request)
        {
            if (completionKind == 0) provider.ReleaseRequest(request!, responseHandled: true);
            else if (completionKind == 1) tracker.ReportAccountRefreshFinished();
            else
            {
                bool finished = tracker.IsFinished(out SnapSyncBatch? next);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(finished, Is.True);
                    Assert.That(next, Is.Null);
                }
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reads, Is.EqualTo(1));
            Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
            Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
        }
    }

    internal static void CompleteAccountPartition(ProgressTracker tracker, SnapProvider provider)
    {
        tracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        {
            tracker.UpdateAccountRangePartitionProgress(ValueKeccak.MaxValue, ValueKeccak.MaxValue, false);
            provider.ReleaseRequest(batch!, true);
        }
    }

    [Test]
    public void Mid_batch_proof_failure_is_attributed_to_serving_peer([Values] bool priorFailures)
    {
        ISnapTree<PathWithStorageSlot> first = StorageTreeMock();
        ISnapTree<PathWithStorageSlot> failed = StorageTreeMock();
        ISnapTree<PathWithStorageSlot> last = StorageTreeMock();
        failed.When(tree => tree.BulkSetAndUpdateRootHash(Arg.Any<IReadOnlyList<PathWithStorageSlot>>()))
            .Do(_ => throw new TrieException("missing peer proof node"));
        Queue<ISnapTree<PathWithStorageSlot>> trees = new([first, failed, last]);
        using IContainer container = CreateContainer(new TestSnapTrieFactory(
            () => throw new NotSupportedException(), () => trees.Dequeue()));
        SnapProvider provider = container.Resolve<SnapProvider>();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        CompleteAccountPartition(tracker, provider);
        PathWithAccount[] accounts =
        [
            new(ValueKeccak.Zero, Account.TotallyEmpty.ToStruct()),
            new(ValueKeccak.Zero.IncrementPath(), Account.TotallyEmpty.ToStruct()),
            new(ValueKeccak.MaxValue, Account.TotallyEmpty.ToStruct())
        ];
        foreach (PathWithAccount account in accounts) tracker.EnqueueAccountStorage(account);
        tracker.IsFinished(out SnapSyncBatch? batch);
        batch!.StorageRangeResponse = new SlotsAndProofs
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(3)
                { ValidSlots(), ValidSlots(), ValidSlots() }
        };
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        PeerInfo peer = new(syncPeer);
        Synchronization.SnapSync.SnapSyncFeed feed = new(provider, LimboLogs.Instance);
        if (priorFailures)
        {
            ISyncPeer honestPeer = Substitute.For<ISyncPeer>();
            honestPeer.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.2", 30303));
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, new PeerInfo(honestPeer));
            for (int i = 0; i < Synchronization.SnapSync.SnapSyncFeed.AllowedInvalidResponses; i++)
                feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer);
        }
        Assert.That(feed.HandleResponse(batch, peer), Is.EqualTo(priorFailures
            ? SyncResponseHandlingResult.LesserQuality : SyncResponseHandlingResult.NoProgress));
        first.Received(1).Commit(Arg.Any<ValueHash256>());
        failed.DidNotReceive().Commit(Arg.Any<ValueHash256>());
        last.Received(1).Commit(Arg.Any<ValueHash256>());
        tracker.IsFinished(out SnapSyncBatch? retry);
        using (retry)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry!.AccountsToRefreshRequest!.Paths.Count, Is.EqualTo(1));
                Assert.That(retry.AccountsToRefreshRequest.Paths[0].PathAndAccount, Is.SameAs(accounts[1]));
            }
            provider.ReleaseRequest(retry, true);
        }
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
    }

    [Test]
    public void Mixed_refresh_completion_apis_retire_all_completed_ownership([Values] bool requestAwareLast)
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        using StorageRange request = CreateRequest(2);
        tracker.EnqueueAccountRefresh(request.Accounts[0], null, null);
        tracker.EnqueueAccountRefresh(request.Accounts[1], null, null);
        tracker.IsFinished(out SnapSyncBatch? first);
        tracker.IsFinished(out SnapSyncBatch? second);
        using (first)
        using (second)
        {
            tracker.ReportAccountRefreshFinished();
            if (requestAwareLast) container.Resolve<SnapProvider>().ReleaseRequest(second!, true);
            else tracker.ReportAccountRefreshFinished();
        }
        tracker.EnqueueAccountRefresh(request.Accounts[0], null, null);
        Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(1));
    }

    [Test]
    public void Verified_refresh_at_origin_preserves_finite_storage_limit()
    {
        using StorageRange request = CreateRequest(1);
        PathWithAccount account = request.Accounts[0];
        using MemDb db = new();
        StateTree source = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        source.Set(account.Path, account.Account.ToAccount());
        source.UpdateRootHash();
        using IContainer container = CreateContainer(root: source.RootHash);
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        ValueHash256 limit = ValueKeccak.MaxValue.DecrementPath();
        tracker.EnqueueAccountRefresh(account, ValueKeccak.Zero, limit);
        tracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        using (AccountsAndProofs response = new()
        {
            PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { account }
        })
        {
            refresh!.AccountsToRefreshRequest!.RootHash = source.RootHash;
            Assert.That(provider.RefreshAccounts(refresh.AccountsToRefreshRequest, response), Is.EqualTo(AddRangeResult.OK));
            provider.ReleaseRequest(refresh, true);
        }
        tracker.IsFinished(out SnapSyncBatch? retry);
        using (retry)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry!.StorageRangeRequest!.StartingHash, Is.EqualTo(ValueKeccak.Zero));
                Assert.That(retry.StorageRangeRequest.LimitHash, Is.EqualTo(limit));
            }
            tracker.OnCompletedLargeStorage(account);
            provider.ReleaseRequest(retry, true);
        }
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
    }

    [Test]
    public void Continuation_bounds_apply_only_to_first_account_in_batch()
    {
        using IContainer container = CreateContainer();
        using StorageRange request = CreateRequest(2);
        request.StartingHash = ValueKeccak.Zero.IncrementPath();
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(2)
            {
                new ArrayPoolList<PathWithStorageSlot>(0), ValidSlots()
            }
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<SnapProvider>().AddStorageRange(request, response), Is.EqualTo(AddRangeResult.EmptyRange));
            Assert.That(container.Resolve<ProgressTracker>().AccountsToRefreshCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Mixed_storage_results_report_worst_failure_in_either_order([Values] bool malformedFirst)
    {
        using IContainer container = CreateContainer();
        using StorageRange request = CreateRequest(3);
        IOwnedReadOnlyList<PathWithStorageSlot> empty = new ArrayPoolList<PathWithStorageSlot>(0);
        IOwnedReadOnlyList<PathWithStorageSlot> malformed = InvalidOrderSlots();
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(3)
            {
                malformedFirst ? malformed : empty,
                malformedFirst ? empty : malformed,
                ValidSlots()
            }
        };

        Assert.That(container.Resolve<SnapProvider>().AddStorageRange(request, response), Is.EqualTo(AddRangeResult.InvalidOrder));
    }

    [TestCase(false, false, -1)]
    [TestCase(true, false, -1)]
    [TestCase(false, true, -1)]
    [TestCase(false, false, 0)]
    [TestCase(false, false, 1199)]
    public void Invalid_storage_batches_bound_and_deduplicate_refreshes(bool duplicateAccounts, bool malformedPrefix, int bogusIndex)
    {
        const int count = 1200;
        using StorageRange request = CreateRequest(count, duplicateAccounts);
        using MemDb serverDb = new();
        TestRawTrieStore serverStore = new(serverDb);
        StateTree source = new(serverStore, LimboLogs.Instance);
        using (serverStore.BeginBlockCommit(1))
        {
            foreach (PathWithAccount account in request.Accounts) source.Set(account.Path, account.Account.ToAccount());
            source.Commit();
        }
        SnapStateServer server = new(serverStore.AsReadOnly(), LimboLogs.Instance);
        using IContainer container = CreateContainer(root: source.RootHash);
        SnapProvider provider = container.Resolve<SnapProvider>();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        CompleteAccountPartition(tracker, provider);
        if (duplicateAccounts)
        {
            ArrayPoolList<PathWithAccount> aliases = (ArrayPoolList<PathWithAccount>)request.Accounts;
            for (int i = 1; i < aliases.Count; i++) aliases[i] = aliases[0];
            Assert.That(provider.AddAccountRange(1, source.RootHash, ValueKeccak.Zero, new[] { aliases[0] }), Is.EqualTo(AddRangeResult.OK));
            for (int i = 1; i < aliases.Count; i++) tracker.EnqueueAccountStorage(aliases[i]);
        }
        else Assert.That(provider.AddAccountRange(1, source.RootHash, ValueKeccak.Zero, request.Accounts), Is.EqualTo(AddRangeResult.OK));
        tracker.IsFinished(out SnapSyncBatch? batch);
        ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> slots = [with(count)];
        for (int i = 0; i < count; i++)
            slots.Add(i == bogusIndex ? new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [2]) }
                : malformedPrefix ? i == count - 1 ? ValidSlots() : InvalidOrderSlots()
                : new ArrayPoolList<PathWithStorageSlot>(0));
        batch!.StorageRangeResponse = new SlotsAndProofs { PathsAndSlots = slots };
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        Synchronization.SnapSync.SnapSyncFeed feed = new(provider, LimboLogs.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(feed.HandleResponse(batch, new PeerInfo(syncPeer)), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            Assert.That(tracker.AccountsToRefreshCount,
                Is.EqualTo(duplicateAccounts ? 1 : SnapProvider.MaxQueuedEmptyStreakRefreshes));
        }

        HashSet<ValueHash256> retried = [];
        int handled = 0;
        int refreshCount = 0;
        while (!tracker.IsFinished(out SnapSyncBatch? next))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(++handled, Is.LessThanOrEqualTo(65));
                Assert.That(tracker.AccountsToRefreshCount, Is.LessThanOrEqualTo(SnapProvider.MaxQueuedEmptyStreakRefreshes));
            }
            using (next)
            {
                Assert.That(next, Is.Not.Null);
                if (next!.AccountRangeRequest is { } range)
                    tracker.UpdateAccountRangePartitionProgress(range.LimitHash!.Value, ValueKeccak.MaxValue, false);
                else if (next.AccountsToRefreshRequest is { } refresh)
                {
                    ValueHash256 path = refresh.Paths[0].PathAndAccount!.Path;
                    Assert.Fail("already authenticated account evidence must complete refresh ownership locally");
                    refreshCount++;
                    (IOwnedReadOnlyList<PathWithAccount> values, IByteArrayList proofs) =
                        server.GetAccountRanges(source.RootHash, path, path.IncrementPath(), 4000, CancellationToken.None);
                    using AccountsAndProofs response = new() { PathAndAccounts = values, Proofs = proofs };
                    Assert.That(provider.RefreshAccounts(refresh, response), Is.EqualTo(AddRangeResult.OK));
                }
                else
                {
                    StorageRange retry = next.StorageRangeRequest!;
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(retry.StartingHash ?? ValueKeccak.Zero, Is.EqualTo(ValueKeccak.Zero));
                        Assert.That(retry.LimitHash ?? ValueKeccak.MaxValue, Is.EqualTo(ValueKeccak.MaxValue));
                    }
                    ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> valid = [with(retry.Accounts.Count)];
                    foreach (PathWithAccount account in retry.Accounts.AsSpan())
                    {
                        Assert.That(retried.Add(account.Path), Is.True, "each failed account is committed once after recovery");
                        valid.Add(ValidSlots());
                    }
                    using SlotsAndProofs response = new() { PathsAndSlots = valid };
                    Assert.That(provider.AddStorageRange(retry, response), Is.EqualTo(AddRangeResult.OK));
                }
                provider.ReleaseRequest(next, true);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retried.Count, Is.EqualTo(duplicateAccounts ? 1 : malformedPrefix ? count - 1 : count));
            Assert.That(refreshCount, Is.Zero);
            if (malformedPrefix) Assert.That(retried.Contains(request.Accounts[count - 1].Path), Is.False);
            Assert.That(tracker.LargeStorageProgressCount, Is.Zero);
        }
    }

    [Test]
    public void Concurrent_refresh_admission_keeps_one_bounded_entry_per_range()
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        using StorageRange request = CreateRequest(129);
        Parallel.For(0, 1024, i => tracker.TryEnqueueAccountRefresh(request.Accounts[i % 129], null, null));
        Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(SnapProvider.MaxQueuedEmptyStreakRefreshes));
    }

    [Test]
    public void Mid_batch_backend_failure_retries_failed_account_and_suffix_only([Values(0, 2, 3)] int retryAccounts)
    {
        ISnapTree<PathWithStorageSlot> first = StorageTreeMock();
        ISnapTree<PathWithStorageSlot> failed = StorageTreeMock();
        failed.When(tree => tree.BulkSetAndUpdateRootHash(Arg.Any<IReadOnlyList<PathWithStorageSlot>>()))
            .Do(_ => throw new IOException("backend unavailable"));
        Queue<ISnapTree<PathWithStorageSlot>> trees = new([first, failed, StorageTreeMock(), StorageTreeMock()]);
        using IContainer container = CreateContainer(new TestSnapTrieFactory(
            () => throw new NotSupportedException(), () => trees.Dequeue()));
        SnapProvider provider = container.Resolve<SnapProvider>();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        tracker.IsFinished(out SnapSyncBatch? accountBatch);
        using (accountBatch)
        {
            tracker.UpdateAccountRangePartitionProgress(ValueKeccak.MaxValue, ValueKeccak.MaxValue, false);
            provider.ReleaseRequest(accountBatch!, true);
        }
        PathWithAccount[] accounts =
        [
            new(ValueKeccak.Zero, Account.TotallyEmpty.ToStruct()),
            new(ValueKeccak.Zero.IncrementPath(), Account.TotallyEmpty.ToStruct()),
            new(ValueKeccak.MaxValue, Account.TotallyEmpty.ToStruct())
        ];
        for (int i = 0; i < accounts.Length; i++)
        {
            if (i < retryAccounts)
                tracker.RetryAccountRefresh(new AccountWithStorageStartingHash
                {
                    PathAndAccount = accounts[i],
                    StorageHashLimit = ValueKeccak.MaxValue
                }, storage: true);
            else tracker.EnqueueAccountStorage(accounts[i]);
        }
        tracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        using (SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(3) { ValidSlots(), ValidSlots(), ValidSlots() }
        })
        {
            Assert.That(() => provider.AddStorageRange(batch!.StorageRangeRequest!, response), Throws.InstanceOf<IOException>());
            provider.ReleaseRequest(batch!, false);
        }
        tracker.IsFinished(out SnapSyncBatch? retry);
        using (retry)
        using (SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(2) { ValidSlots(), ValidSlots() }
        })
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry!.StorageRangeRequest!.Accounts.Count, Is.EqualTo(2));
                Assert.That(retry.StorageRangeRequest.Accounts[0], Is.SameAs(accounts[1]));
                Assert.That(retry.StorageRangeRequest.Accounts[1], Is.SameAs(accounts[2]));
                Assert.That(provider.AddStorageRange(retry.StorageRangeRequest, response), Is.EqualTo(AddRangeResult.OK));
            }
            provider.ReleaseRequest(retry, true);
        }
        first.Received(1).Commit(Arg.Any<ValueHash256>());
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
        tracker.EnqueueAccountRefresh(accounts[0], null, null);
        Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(1), "the completed prefix must release retry ownership");
    }

    [Test]
    public void Bottom_fallback_local_refresh_completion_dispatches_woken_storage_in_same_call([Values] bool emptied)
    {
        using StorageRange original = CreateRequest(64);
        using MemDb db = new();
        TestRawTrieStore store = new(db);
        StateTree state = new(store, LimboLogs.Instance);
        using (store.BeginBlockCommit(1))
        {
            foreach (PathWithAccount account in original.Accounts)
                state.Set(account.Path, emptied ? new Account(0, 1) : account.Account.ToAccount());
            state.Commit();
        }
        SnapStateServer server = new(store.AsReadOnly(), LimboLogs.Instance);
        using IContainer container = CreateContainer(root: state.RootHash);
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        foreach (PathWithAccount account in original.Accounts) tracker.EnqueueAccountRefresh(account, null, null);
        List<SnapSyncBatch> active = [];
        try
        {
            for (int i = 0; i < 64; i++)
            {
                tracker.IsFinished(out SnapSyncBatch? refresh);
                Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);
                active.Add(refresh);
            }
            foreach (SnapSyncBatch refresh in active)
            {
                AccountsToRefreshRequest request = refresh.AccountsToRefreshRequest!;
                ValueHash256 path = request.Paths[0].PathAndAccount!.Path;
                (IOwnedReadOnlyList<PathWithAccount> values, IByteArrayList proofs) =
                    server.GetAccountRanges(state.RootHash, path, path.IncrementPath(), 4000, CancellationToken.None);
                using AccountsAndProofs response = new() { PathAndAccounts = values, Proofs = proofs };
                Assert.That(provider.RefreshAccounts(request, response), Is.EqualTo(AddRangeResult.OK));
                provider.ReleaseRequest(refresh, false);
            }
        }
        finally
        {
            foreach (SnapSyncBatch refresh in active) refresh.Dispose();
        }
        foreach (PathWithAccount account in original.Accounts) tracker.EnqueueAccountStorage(account);
        Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(64));
        bool finished = tracker.IsFinished(out SnapSyncBatch? batch);
        if (emptied)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(finished, Is.True);
                Assert.That(batch, Is.Null);
                Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
            }
        }
        else
        {
            Assert.That(finished, Is.False);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(batch!.StorageRangeRequest!.Accounts, Has.Count.EqualTo(64));
                Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
            }
            provider.ReleaseRequest(batch, true);
            batch.Dispose();
            Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
        }
    }

    [Test]
    public void Blocked_bulk_and_continuation_scan_is_capped_at_1200_each([Values] bool completeLocally)
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        int queueSize = completeLocally ? 1200 : 1201;
        using StorageRange accounts = CreateRequest(64 + 2 * queueSize);
        for (int i = 0; i < 64; i++) tracker.EnqueueAccountRefresh(accounts.Accounts[i], null, null);
        HashSet<PathWithAccount> continuation = [];
        HashSet<PathWithAccount> bulk = [];
        for (int i = 64; i < accounts.Accounts.Count; i++)
        {
            bool finite = i < 64 + queueSize;
            (finite ? continuation : bulk).Add(accounts.Accounts[i]);
            tracker.EnqueueAccountRefresh(accounts.Accounts[i], finite ? ValueKeccak.Zero.IncrementPath() : ValueKeccak.Zero, null);
        }
        List<SnapSyncBatch> active = [];
        try
        {
            for (int i = 0; i < 4; i++)
            {
                tracker.IsFinished(out SnapSyncBatch? refresh);
                Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);
                active.Add(refresh);
            }
            if (completeLocally)
            {
                foreach (SnapSyncBatch refresh in active)
                {
                    provider.ReleaseRequest(refresh, false);
                    refresh.Dispose();
                }
                active.Clear();
            }
            int continuationInspections = 0;
            int bulkInspections = 0;
            int localCompletions = 0;
            HashSet<PathWithAccount> examined = [];
            tracker.SetAccountEvidence(account =>
            {
                examined.Add(account);
                if (continuation.Contains(account)) continuationInspections++;
                else if (bulk.Contains(account)) bulkInspections++;
                else if (completeLocally) localCompletions++;
                return completeLocally && (localCompletions == 64 || !continuation.Contains(account) && !bulk.Contains(account))
                    ? (AccountStruct.TotallyEmpty, tracker.CurrentRoot!.ValueHash256) : null;
            }, static (_, _, _) => { });
            bool finished = tracker.IsFinished(out SnapSyncBatch? fallback);
            if (completeLocally)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(finished, Is.True);
                    Assert.That(fallback, Is.Null);
                    Assert.That(localCompletions, Is.EqualTo(64));
                    Assert.That(continuationInspections, Is.EqualTo(2400));
                    Assert.That(bulkInspections, Is.EqualTo(2400));
                    Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
                    Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
                }
            }
            else
            {
                Assert.That(fallback!.AccountsToRefreshRequest, Is.Not.Null);
                active.Add(fallback);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(continuationInspections, Is.EqualTo(1200));
                    Assert.That(bulkInspections, Is.EqualTo(1200));
                    Assert.That(examined, Does.Not.Contain(accounts.Accounts[64 + queueSize - 1]));
                    Assert.That(examined, Does.Not.Contain(accounts.Accounts[^1]));
                    Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(59));
                    Assert.That(tracker.IsSnapGetRangesFinished(), Is.False);
                }
            }
        }
        finally
        {
            foreach (SnapSyncBatch refresh in active)
            {
                provider.ReleaseRequest(refresh, true);
                refresh.Dispose();
            }
        }
    }

    [Test]
    public void Full_refresh_cap_falls_through_to_codes_then_refresh_in_same_call([Values] bool continuation)
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        using StorageRange source = CreateRequest(65);
        ValueHash256 start = continuation ? ValueKeccak.Zero.IncrementPath() : ValueKeccak.Zero;
        foreach (PathWithAccount account in source.Accounts) tracker.EnqueueAccountRefresh(account, start, null);
        List<SnapSyncBatch> active = [];
        try
        {
            for (int i = 0; i < 4; i++)
            {
                tracker.IsFinished(out SnapSyncBatch? refresh);
                active.Add(refresh!);
                AssertRefresh(refresh!, i);
            }
            tracker.EnqueueCodeHash(TestItem.KeccakA);
            tracker.IsFinished(out SnapSyncBatch? codes);
            Assert.That(codes!.CodesRequest, Is.EqualTo(new[] { TestItem.KeccakA }));
            provider.ReleaseRequest(codes, true);
            codes.Dispose();
            for (int i = 0; i < 4; i++)
            {
                tracker.IsFinished(out SnapSyncBatch? refresh);
                active.Add(refresh!);
                AssertRefresh(refresh!, i + 4);
            }
            tracker.IsFinished(out SnapSyncBatch? fallback);
            AssertRefresh(fallback!, 8);
            active.Add(fallback!);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(55));
                Assert.That(tracker.IsSnapGetRangesFinished(), Is.False);
            }
        }
        finally
        {
            foreach (SnapSyncBatch refresh in active)
            {
                provider.ReleaseRequest(refresh, true);
                refresh.Dispose();
            }
        }

        void AssertRefresh(SnapSyncBatch? batch, int index)
        {
            Assert.That(batch?.AccountsToRefreshRequest?.Paths.Count, Is.EqualTo(1));
            AccountsToRefreshRequest refresh = batch!.AccountsToRefreshRequest!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(refresh.Paths[0].PathAndAccount, Is.SameAs(source.Accounts[index]));
                Assert.That(refresh.Paths[0].StorageStartingHash, Is.EqualTo(start));
                Assert.That(refresh.Paths[0].StorageHashLimit, Is.EqualTo(ValueKeccak.MaxValue));
                Assert.That(batch.StorageRangeRequest, Is.Null);
                Assert.That(batch.CodesRequest, Is.Null);
                Assert.That(batch.AccountRangeRequest, Is.Null);
            }
        }
    }

    [Test]
    public void Same_account_split_and_distinct_identity_aliases_refresh_once_at_latest_dequeue_root([Values] bool sharedIdentity, [Values] bool emptied)
    {
        using StorageRange source = CreateRequest(1);
        PathWithAccount first = source.Accounts[0];
        PathWithAccount second = sharedIdentity ? first : new(first.Path, first.Account);
        ValueHash256 originalStorageRoot = first.Account!.StorageRoot;
        using MemDb db = new();
        StorageTree storage = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        storage.Set(ValueKeccak.Zero, [2], false);
        storage.UpdateRootHash();
        Account refreshed = emptied ? new Account(0, 1) : first.Account!.ToAccount().WithChangedStorageRoot(storage.RootHash);
        StateTree state = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        state.Set(first.Path, refreshed);
        state.UpdateRootHash();
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        ValueHash256 finiteLimit = ValueKeccak.MaxValue.DecrementPath();
        tracker.EnqueueAccountRefresh(first, ValueKeccak.Zero, ValueKeccak.MaxValue);
        tracker.EnqueueAccountRefresh(second, ValueKeccak.Zero, finiteLimit);
        Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(1));
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        BlockHeader pivot = Build.A.BlockHeader.WithNumber(129).WithStateRoot(state.RootHash).TestObject;
        blockTree.BestSuggestedHeader.Returns(pivot);
        blockTree.FindHeader(Arg.Any<ulong>()).Returns(pivot);
        provider.UpdatePivot();
        tracker.IsFinished(out SnapSyncBatch? refresh);
        Assert.That(refresh!.AccountsToRefreshRequest!.RootHash, Is.EqualTo(state.RootHash));
        using AccountsAndProofs response = new()
        {
            PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(first.Path, refreshed.ToStruct()) }
        };
        Assert.That(provider.RefreshAccounts(refresh.AccountsToRefreshRequest, response), Is.EqualTo(AddRangeResult.OK));
        provider.ReleaseRequest(refresh, true);
        refresh.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Account!.StorageRoot, Is.EqualTo(originalStorageRoot));
            Assert.That(second.Account!.StorageRoot, Is.EqualTo(first.Account.StorageRoot));
        }
        int resumed = 0;
        while (!tracker.IsFinished(out SnapSyncBatch? batch))
        {
            Assert.That(batch!.AccountsToRefreshRequest, Is.Null, "aliases share one anchored refresh");
            StorageRange range = batch.StorageRangeRequest!;
            Assert.That(range.RootHash, Is.EqualTo(state.RootHash));
            resumed += range.Accounts.Count;
            using SlotsAndProofs slots = new()
            {
                PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
                {
                    new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [2]) }
                }
            };
            Assert.That(provider.AddStorageRange(range, slots), Is.EqualTo(AddRangeResult.OK));
            provider.ReleaseRequest(batch, true);
            batch.Dispose();
        }
        Assert.That(resumed, Is.EqualTo(emptied ? 0 : 2));
    }

    [Test]
    public void Empty_or_deleted_refresh_completed_after_pivot_move_preserves_all_waiters(
        [Values] bool deleted, [Values] bool movePivot)
    {
        using StorageRange source = CreateRequest(1);
        PathWithAccount first = source.Accounts[0];
        PathWithAccount alias = new(first.Path, first.Account);
        ValueHash256 limit = ValueKeccak.MaxValue.DecrementPath();
        using MemDb db = new();
        TestRawTrieStore store = new(db);
        StateTree state = new(store, LimboLogs.Instance);
        using (store.BeginBlockCommit(1))
        {
            state.Set(deleted ? first.Path.IncrementPath() : first.Path, new Account(0, 1));
            state.Commit();
        }
        Hash256 emptyRoot = state.RootHash;
        SnapStateServer server = new(store.AsReadOnly(), LimboLogs.Instance);
        using IContainer container = CreateContainer(root: emptyRoot);
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        tracker.EnqueueAccountRefresh(first, ValueKeccak.Zero, ValueKeccak.MaxValue);
        tracker.EnqueueAccountRefresh(alias, ValueKeccak.Zero, limit);
        tracker.IsFinished(out SnapSyncBatch? refresh);
        Assert.That(refresh!.AccountsToRefreshRequest!.RootHash, Is.EqualTo(emptyRoot));

        if (movePivot)
        {
            using (store.BeginBlockCommit(2))
            {
                state.Set(first.Path, first.Account.ToAccount());
                state.Commit();
            }
            BlockHeader pivot = Build.A.BlockHeader.WithNumber(129).WithStateRoot(state.RootHash).TestObject;
            IBlockTree blockTree = container.Resolve<IBlockTree>();
            blockTree.BestSuggestedHeader.Returns(pivot);
            blockTree.FindHeader(Arg.Any<ulong>()).Returns(pivot);
            provider.UpdatePivot();
        }

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            server.GetAccountRanges(emptyRoot, first.Path, first.Path.IncrementPath(), 4000, CancellationToken.None);
        using (AccountsAndProofs response = new() { PathAndAccounts = accounts, Proofs = proofs })
            Assert.That(provider.RefreshAccounts(refresh.AccountsToRefreshRequest, response), Is.EqualTo(AddRangeResult.OK));
        provider.ReleaseRequest(refresh, true);
        refresh.Dispose();
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.EqualTo(!movePivot));
        if (!movePivot) return;

        tracker.IsFinished(out SnapSyncBatch? currentRefresh);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(currentRefresh!.AccountsToRefreshRequest!.RootHash, Is.EqualTo(state.RootHash));
            Assert.That(currentRefresh.AccountsToRefreshRequest.Paths.Count, Is.EqualTo(1));
        }
        (accounts, proofs) = server.GetAccountRanges(state.RootHash, first.Path, first.Path.IncrementPath(), 4000, CancellationToken.None);
        using (AccountsAndProofs response = new() { PathAndAccounts = accounts, Proofs = proofs })
            Assert.That(provider.RefreshAccounts(currentRefresh.AccountsToRefreshRequest, response), Is.EqualTo(AddRangeResult.OK));
        provider.ReleaseRequest(currentRefresh, true);
        currentRefresh.Dispose();

        HashSet<ValueHash256> limits = [];
        int resumed = 0;
        while (!tracker.IsFinished(out SnapSyncBatch? batch))
        {
            Assert.That(++resumed, Is.LessThanOrEqualTo(2));
            StorageRange range = batch!.StorageRangeRequest!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(range.RootHash, Is.EqualTo(state.RootHash));
                Assert.That(range.Accounts.Count, Is.EqualTo(1));
                Assert.That(range.StartingHash ?? ValueKeccak.Zero, Is.EqualTo(ValueKeccak.Zero));
                Assert.That(limits.Add(range.LimitHash ?? ValueKeccak.MaxValue), Is.True);
            }
            using SlotsAndProofs slots = new()
            {
                PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1) { ValidSlots() }
            };
            Assert.That(provider.AddStorageRange(range, slots), Is.EqualTo(AddRangeResult.OK));
            provider.ReleaseRequest(batch, true);
            batch.Dispose();
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed, Is.EqualTo(2));
            Assert.That(limits, Is.EquivalentTo(new[] { ValueKeccak.MaxValue, limit }));
            Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
            Assert.That(tracker.LargeStorageProgressCount, Is.Zero);
            Assert.That(first.Account, Is.EqualTo(alias.Account));
        }
    }

    [Test]
    public void Stale_root_mismatch_cannot_override_current_root_empty_range([Values] bool staleFirst)
    {
        using StorageRange source = CreateRequest(2);
        PathWithAccount stale = source.Accounts[staleFirst ? 0 : 1];
        PathWithAccount current = source.Accounts[staleFirst ? 1 : 0];
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        VerifyAccountEvidence(provider, stale);
        Hash256 currentRoot = VerifyAccountEvidence(provider, current);
        CompleteAccountPartition(tracker, provider);
        source.RootHash = currentRoot;
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(2)
            {
                staleFirst ? new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [2]) } : new ArrayPoolList<PathWithStorageSlot>(0),
                staleFirst ? new ArrayPoolList<PathWithStorageSlot>(0) : new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [2]) }
            }
        };
        AddRangeResult result = provider.AddStorageRangeWithResult(source, response, out long bytes, out bool neutral, out bool rootedBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AddRangeResult.EmptyRange));
            Assert.That(neutral, Is.False);
            Assert.That(bytes, Is.Zero);
            Assert.That(rootedBytes, Is.False);
            Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(2));
        }
    }

    internal static Hash256 VerifyAccountEvidence(SnapProvider provider, PathWithAccount account)
    {
        using MemDb db = new();
        StateTree state = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        state.Set(account.Path, account.Account.ToAccount());
        state.UpdateRootHash();
        using AccountsToRefreshRequest request = new()
        {
            RootHash = state.RootHash,
            Paths = new ArrayPoolList<AccountWithStorageStartingHash>(1)
            {
                new() { PathAndAccount = account, StorageHashLimit = ValueKeccak.MaxValue }
            }
        };
        using AccountsAndProofs response = new()
        {
            PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(account.Path, account.Account) }
        };
        Assert.That(provider.RefreshAccounts(request, response), Is.EqualTo(AddRangeResult.OK));
        return state.RootHash;
    }

    private static ISnapTree<PathWithStorageSlot> StorageTreeMock()
    {
        ISnapTree<PathWithStorageSlot> tree = Substitute.For<ISnapTree<PathWithStorageSlot>>();
        tree.RootHash.Returns(Keccak.EmptyTreeHash);
        return tree;
    }

    internal static IContainer CreateContainer(ISnapTrieFactory? factory = null, Hash256? root = null)
    {
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
            .WithSuggestedHeaderOfStateRoot(root ?? Keccak.EmptyTreeHash);
        if (factory is not null) builder.AddSingleton<ISnapTrieFactory>(factory);
        return builder.Build();
    }

    internal static StorageRange CreateRequest(int count, bool duplicateAccounts = false)
    {
        using MemDb db = new();
        StorageTree storage = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        storage.Set(ValueKeccak.Zero, [1], false);
        storage.UpdateRootHash();
        Account account = new(0, 0, storage.RootHash, Keccak.OfAnEmptyString);
        ArrayPoolList<PathWithAccount> accounts = [with(count)];
        ValueHash256 path = ValueKeccak.Zero;
        for (int i = 0; i < count; i++)
        {
            accounts.Add(new(path, account.ToStruct()));
            if (!duplicateAccounts) path = path.IncrementPath();
        }
        return new StorageRange { Accounts = accounts };
    }

    internal static byte[] CreateSingleSlotProof(byte[] value)
    {
        using MemDb proofDb = new();
        RawScopedTrieStore proofStore = new(proofDb);
        StorageTree storage = new(proofStore, LimboLogs.Instance);
        storage.Set(ValueKeccak.Zero, value, false);
        storage.Commit();
        byte[] proof = proofStore.LoadRlp(TreePath.Empty, storage.RootHash)!;
        Assert.That(ValueKeccak.Compute(proof), Is.EqualTo(storage.RootHash.ValueHash256));
        return proof;
    }

    private static IOwnedReadOnlyList<PathWithStorageSlot> InvalidOrderSlots() =>
        new ArrayPoolList<PathWithStorageSlot>(2) { new(ValueKeccak.MaxValue, [1]), new(ValueKeccak.Zero, [1]) };

    internal static IOwnedReadOnlyList<PathWithStorageSlot> ValidSlots() =>
        new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [1]) };

    [Test]
    public void Batched_fallback_owns_accounts_until_request_release([Values] bool responseHandled, [Values] bool emptyReply)
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        using StorageRange accounts = CreateRequest(2);
        foreach (PathWithAccount account in accounts.Accounts)
        {
            tracker.RetryAccountRefresh(new AccountWithStorageStartingHash
            {
                PathAndAccount = account,
                StorageHashLimit = ValueKeccak.MaxValue
            }, storage: true);
            tracker.EnqueueAccountRefresh(account, null, null);
            tracker.EnqueueAccountStorage(account);
        }
        Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
        tracker.IsFinished(out SnapSyncBatch? active);
        using (active)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(active?.StorageRangeRequest, Is.Not.Null);
                Assert.That(active!.StorageRangeRequest!.Accounts.Count, Is.EqualTo(2));
            }
            foreach (PathWithAccount account in accounts.Accounts) tracker.EnqueueAccountRefresh(account, null, null);
            if (emptyReply)
            {
                using SlotsAndProofs response = new();
                Assert.That(provider.AddStorageRange(active.StorageRangeRequest, response), Is.EqualTo(AddRangeResult.ExpiredRootHash));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
                Assert.That(tracker.IsFinished(out SnapSyncBatch? waiting), Is.False);
                Assert.That(waiting, Is.Null);
            }
            provider.ReleaseRequest(active, responseHandled);
        }
        HashSet<ValueHash256> retried = [];
        while (!tracker.IsFinished(out SnapSyncBatch? next))
        {
            using (next)
            {
                Assert.That(next, Is.Not.Null);
                if (responseHandled)
                    Assert.That(retried.Add(next!.AccountsToRefreshRequest!.Paths[0].PathAndAccount!.Path), Is.True);
                else
                    foreach (PathWithAccount account in next!.StorageRangeRequest!.Accounts)
                        Assert.That(retried.Add(account.Path), Is.True);
                provider.ReleaseRequest(next!, true);
            }
        }
        Assert.That(retried.Count, Is.EqualTo(2));
    }

    [Test]
    public void Overflow_full_ranges_leave_room_for_account_progress_and_one_storage_batch()
    {
        using StorageRange accounts = CreateRequest(1199);
        using MemDb serverDb = new();
        TestRawTrieStore store = new(serverDb);
        StateTree source = new(store, LimboLogs.Instance);
        using (store.BeginBlockCommit(1))
        {
            foreach (PathWithAccount account in accounts.Accounts) source.Set(account.Path, account.Account.ToAccount());
            source.Commit();
        }
        SnapStateServer server = new(store.AsReadOnly(), LimboLogs.Instance);
        using IContainer container = CreateContainer(root: source.RootHash);
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        tracker.IsFinished(out SnapSyncBatch? initial);
        using (initial)
        {
            foreach (PathWithAccount account in accounts.Accounts)
                tracker.EnqueueAccountRefresh(account, null, null);
            ValueHash256 start = accounts.Accounts[64].Path;
            (IOwnedReadOnlyList<PathWithAccount> values, IByteArrayList proofs) =
                server.GetAccountRanges(source.RootHash, start, ValueKeccak.MaxValue, 2_000_000, CancellationToken.None);
            using AccountsAndProofs verified = new() { PathAndAccounts = values, Proofs = proofs };
            Assert.That(provider.AddAccountRange(1, source.RootHash, start, accounts.Accounts.AsSpan()[64..].ToArray(), verified.Proofs), Is.EqualTo(AddRangeResult.OK));
            tracker.UpdateAccountRangePartitionProgress(ValueKeccak.MaxValue, ValueKeccak.Zero.IncrementPath(), true);
            provider.ReleaseRequest(initial!, true);
        }

        int refreshes = 0;
        int storageBatches = 0;
        int accountsRetried = 0;
        bool accountProgress = false;
        while (!tracker.IsFinished(out SnapSyncBatch? next))
        {
            using (next)
            {
                Assert.That(next, Is.Not.Null);
                if (next!.AccountsToRefreshRequest is not null)
                    Assert.That(++refreshes, Is.LessThanOrEqualTo(64));
                else if (next.AccountRangeRequest is { } range)
                {
                    accountProgress = true;
                    tracker.UpdateAccountRangePartitionProgress(range.LimitHash!.Value, ValueKeccak.MaxValue, false);
                }
                else
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(accountProgress, Is.True, "storage retries must not starve the ready account partition");
                        Assert.That(++storageBatches, Is.EqualTo(1));
                    }
                    accountsRetried += next.StorageRangeRequest!.Accounts.Count;
                }
                provider.ReleaseRequest(next, true);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshes, Is.EqualTo(64));
            Assert.That(storageBatches, Is.EqualTo(1));
            Assert.That(accountsRetried, Is.EqualTo(1135));
            Assert.That(accountProgress, Is.True);
        }
    }

    [Test]
    public void Verified_refreshes_batch_full_ranges_and_preserve_finite_limits([Values] bool finiteLimit)
    {
        using StorageRange accounts = CreateRequest(2);
        using MemDb serverDb = new();
        TestRawTrieStore serverStore = new(serverDb);
        StateTree source = new(serverStore, LimboLogs.Instance);
        using (serverStore.BeginBlockCommit(1))
        {
            foreach (PathWithAccount account in accounts.Accounts) source.Set(account.Path, account.Account.ToAccount());
            source.Commit();
        }
        SnapStateServer server = new(serverStore.AsReadOnly(), LimboLogs.Instance);
        using IContainer container = CreateContainer(root: source.RootHash);
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        ValueHash256 limit = finiteLimit ? ValueKeccak.MaxValue.DecrementPath() : ValueKeccak.MaxValue;
        foreach (PathWithAccount account in accounts.Accounts)
            tracker.EnqueueAccountRefresh(account, ValueKeccak.Zero, limit);
        for (int i = 0; i < 2; i++)
        {
            tracker.IsFinished(out SnapSyncBatch? refresh);
            using (refresh)
            {
                AccountsToRefreshRequest request = refresh!.AccountsToRefreshRequest!;
                request.RootHash = source.RootHash;
                ValueHash256 path = request.Paths[0].PathAndAccount!.Path;
                (IOwnedReadOnlyList<PathWithAccount> values, IByteArrayList proofs) =
                    server.GetAccountRanges(source.RootHash, path, path.IncrementPath(), 4000, CancellationToken.None);
                using AccountsAndProofs response = new() { PathAndAccounts = values, Proofs = proofs };
                Assert.That(provider.RefreshAccounts(request, response), Is.EqualTo(AddRangeResult.OK));
                provider.ReleaseRequest(refresh, true);
            }
        }

        int requests = 0;
        int retriedAccounts = 0;
        while (!tracker.IsFinished(out SnapSyncBatch? next))
        {
            using (next)
            {
                StorageRange storage = next!.StorageRangeRequest!;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(++requests, Is.LessThanOrEqualTo(finiteLimit ? 2 : 1));
                    Assert.That(storage.StartingHash ?? ValueKeccak.Zero, Is.EqualTo(ValueKeccak.Zero));
                    Assert.That(storage.LimitHash ?? ValueKeccak.MaxValue, Is.EqualTo(limit));
                }
                ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> slots = [with(storage.Accounts.Count)];
                foreach (PathWithAccount account in storage.Accounts)
                {
                    retriedAccounts++;
                    slots.Add(ValidSlots());
                }
                using SlotsAndProofs response = new() { PathsAndSlots = slots };
                Assert.That(provider.AddStorageRange(storage, response), Is.EqualTo(AddRangeResult.OK));
                provider.ReleaseRequest(next, true);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retriedAccounts, Is.EqualTo(2));
            Assert.That(requests, Is.EqualTo(finiteLimit ? 2 : 1));
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Returning_duplicate_storage_work_allows_disposal_to_reenter_tracker(
        [Values] bool finiteLimit, CancellationToken cancellationToken)
    {
        using IContainer container = CreateContainer();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        CompleteAccountPartition(tracker, provider);
        using StorageRange source = CreateRequest(1);
        ValueHash256 limit = finiteLimit ? ValueKeccak.MaxValue.DecrementPath() : ValueKeccak.MaxValue;
        PathWithAccount account = source.Accounts[0];
        tracker.RetryAccountRefresh(new AccountWithStorageStartingHash
        {
            PathAndAccount = account,
            StorageHashLimit = limit
        }, storage: true);
        Assert.That(tracker.IsFinished(out SnapSyncBatch? storage), Is.False);
        using SnapSyncBatch active = storage!;
        StorageRange duplicate = source.Copy();
        duplicate.LimitHash = limit;
        TaskCompletionSource disposalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim disposalRelease = new(false);
        Task reentry = Task.CompletedTask;
        DisposalCallback accounts = new(duplicate.Accounts, () =>
        {
            disposalEntered.TrySetResult();
            disposalRelease.Wait();
        });
        duplicate.Accounts = accounts;

        Task returning = Task.Factory.StartNew(() => tracker.EnqueueNextSlot(duplicate),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Task first = await Task.WhenAny(disposalEntered.Task, returning).WaitAsync(cancellationToken);
            if (first == returning) await returning;
            Assert.That(disposalEntered.Task.IsCompleted, Is.True, "duplicate storage work must enter owned-list disposal");
            reentry = Task.Factory.StartNew(() => tracker.EnqueueAccountRefresh(account, ValueKeccak.Zero, limit),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await reentry.WaitAsync(cancellationToken);
        }
        finally
        {
            disposalRelease.Set();
            await Task.WhenAll(returning, reentry);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accounts.DisposeCount, Is.EqualTo(1));
            Assert.That(active.StorageRangeRequest!.Accounts.Count, Is.EqualTo(1));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(active.StorageRangeRequest.Accounts[0].Path, Is.EqualTo(account.Path));
            Assert.That(active.StorageRangeRequest.LimitHash ?? ValueKeccak.MaxValue, Is.EqualTo(limit));
        }
        provider.ReleaseRequest(active, true);
        Assert.That(tracker.IsFinished(out SnapSyncBatch? refresh), Is.False);
        using (refresh)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(refresh!.AccountsToRefreshRequest!.Paths.Count, Is.EqualTo(1));
                Assert.That(refresh.AccountsToRefreshRequest.Paths[0].PathAndAccount!.Path, Is.EqualTo(account.Path));
            }
            provider.ReleaseRequest(refresh, true);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.IsFinished(out SnapSyncBatch? remaining), Is.True);
            Assert.That(remaining, Is.Null);
        }
    }

    private sealed class DisposalCallback(IOwnedReadOnlyList<PathWithAccount> inner, Action callback) : IOwnedReadOnlyList<PathWithAccount>
    {
        public int DisposeCount { get; private set; }
        public int Count => inner.Count;
        public PathWithAccount this[int index] => inner[index];
        public ReadOnlySpan<PathWithAccount> AsSpan() => inner.AsSpan();
        public IEnumerator<PathWithAccount> GetEnumerator() => inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose()
        {
            DisposeCount++;
            try { callback(); }
            finally { inner.Dispose(); }
        }
    }
}
