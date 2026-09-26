// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Proofs;
using Nethermind.State.SnapServer;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.Trie.Pruning;
using Nethermind.Trie;
using AccountRange = Nethermind.State.Snap.AccountRange;
using NSubstitute;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapProviderTests
{

    private ContainerBuilder CreateContainerBuilder(
        TestSyncConfig? testSyncConfig = null,
        Func<ILogManager, ISnapTrieFactory>? factoryCreator = null) =>
        new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(testSyncConfig ?? new TestSyncConfig(), factoryCreator))
            .AddSingleton<ISnapTestHelper, FlatSnapTestHelper>();

    private IContainer CreateContainer(TestSyncConfig? testSyncConfig = null) =>
        CreateContainerBuilder(testSyncConfig).Build();

    [Test]
    public void AddAccountRange_AccountListIsEmpty_ThrowArgumentException()
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        Assert.That(
            () => snapProvider.AddAccountRange(
                0,
                Keccak.Zero,
                Keccak.Zero,
                Array.Empty<PathWithAccount>(),
                EmptyByteArrayList.Instance), Throws.ArgumentException);
    }

    [Test]
    public void AddAccountRange_ResponseHasEmptyListOfAccountsAndOneProof_ReturnsExpiredRootHash()
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        using AccountsAndProofs accountsAndProofs = new();
        AccountRange accountRange = new(Keccak.Zero, Keccak.Zero, Keccak.MaxValue);
        accountsAndProofs.PathAndAccounts = new List<PathWithAccount>().ToPooledList();
        accountsAndProofs.Proofs = new ByteArrayListAdapter(new List<byte[]> { new byte[] { 0x0 } }.ToPooledList());

        Assert.That(snapProvider.AddAccountRange(accountRange, accountsAndProofs), Is.EqualTo(AddRangeResult.ExpiredRootHash));
    }

    [Test]
    public void AddStorageRange_ResponseReversedOrderedListOfAccounts_ReturnsInvalidOrder()
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        StorageRange storage = new()
        {
            Accounts = new PathWithAccount[] { new(TestItem.KeccakA, Account.TotallyEmpty) }.ToPooledList(),
        };
        List<PathWithStorageSlot> slots =
        [
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000004"), []),
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000003"), []),
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000002"), []),
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000001"), []),
        ];

        Assert.That(snapProvider.AddStorageRangeForAccount(
            storage,
            0,
            slots,
            null), Is.EqualTo(AddRangeResult.InvalidOrder));

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.False);
    }

    [Test]
    public void AddStorageRange_EmptySlotsList_ReturnsEmptySlots()
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        StorageRange storage = new()
        {
            Accounts = new PathWithAccount[] { new(TestItem.KeccakA, Account.TotallyEmpty) }.ToPooledList(),
        };

        // Test with empty slots list
        List<PathWithStorageSlot> emptySlots = [];

        Assert.That(snapProvider.AddStorageRangeForAccount(
            storage,
            0,
            emptySlots,
            null), Is.EqualTo(AddRangeResult.EmptyRange));

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.False);
    }

    [TestCase(1, 2, AddRangeResult.OutOfBounds)]
    [TestCase(2, 3, AddRangeResult.OutOfBounds)]
    [TestCase(4, 64, AddRangeResult.OutOfBounds)]
    [TestCase(1, 1, AddRangeResult.EmptyRange)]
    [TestCase(4, 4, AddRangeResult.EmptyRange)]
    [TestCase(4, 2, AddRangeResult.EmptyRange)]
    public void AddStorageRange_RejectsResponseOnlyWhenSlotListsExceedRequestedAccounts(int accountCount, int slotListCount, AddRangeResult expected)
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        using StorageRange request = CreateStorageRange(accountCount);
        using SlotsAndProofs response = CreateEmptySlotsResponse(slotListCount);

        Assert.That(snapProvider.AddStorageRange(request, response), Is.EqualTo(expected));
    }

    [Test]
    public void AddStorageRange_ResponseHasMoreSlotListsThanRequestedAccounts_KeepsRangePhaseCompletable()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 1
        })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        PathWithAccount account = new(TestItem.ValueKeccaks[0], Account.TotallyEmpty);
        progressTracker.EnqueueAccountStorage(account);

        DrainAccountRangePartition(progressTracker);

        progressTracker.IsFinished(out SnapSyncBatch? storageBatch);
        storageBatch!.StorageRangeResponse = CreateEmptySlotsResponse(storageBatch.StorageRangeRequest!.Accounts.Count + 1);

        Assert.That(snapProvider.AddStorageRange(storageBatch.StorageRangeRequest, storageBatch.StorageRangeResponse), Is.EqualTo(AddRangeResult.OutOfBounds));
        snapProvider.ReleaseRequest(storageBatch, responseHandled: true);
        storageBatch.Dispose();

        progressTracker.IsFinished(out SnapSyncBatch? retryBatch);
        Assert.That(retryBatch!.StorageRangeRequest!.Accounts.AsSpan()[0].Path, Is.EqualTo(account.Path));

        progressTracker.ReportStorageRequestFinished(retryBatch.StorageRangeRequest.Accounts.Count);
        retryBatch.Dispose();

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True);
    }

    [Test]
    public void AddStorageRange_ResponseCoversFewerAccountsThanRequested_QueuesTheRestAgain()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        PathWithAccount covered = new(TestItem.ValueKeccaks[0], Account.TotallyEmpty);
        PathWithAccount uncovered = new(TestItem.ValueKeccaks[1], Account.TotallyEmpty);
        progressTracker.EnqueueAccountStorage(covered);
        progressTracker.EnqueueAccountStorage(uncovered);
        DrainAccountRangePartition(progressTracker);

        progressTracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        {
            Assert.That(batch!.StorageRangeRequest!.Accounts.Count, Is.EqualTo(2));
            batch.StorageRangeResponse = CreateEmptySlotsResponse(1);

            snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse);
            snapProvider.ReleaseRequest(batch, responseHandled: true);
        }

        // The covered account failed verification and goes for a refresh; the uncovered one comes back.
        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);
            progressTracker.ReportAccountRefreshFinished();
        }

        progressTracker.IsFinished(out SnapSyncBatch? retried);
        using (retried)
        {
            Assert.That(retried!.StorageRangeRequest!.Accounts.AsSpan()[0].Path, Is.EqualTo(uncovered.Path));
            progressTracker.ReportStorageRequestFinished(retried.StorageRangeRequest.Accounts.Count);
        }

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True);
    }

    // Regression for #13155: a storage range that keeps coming back empty (no slots, no proof) was re-queued at the
    // next pivot forever. A geth peer answers exactly like that for an account that has no storage at the requested
    // root, so the same account was asked for at every new pivot with no way out. After a streak that outlives the
    // feed's own pivot refreshes the account must be re-proven at the current pivot instead of being re-requested.
    [TestCase(1)]
    [TestCase(3)]
    public void AddStorageRange_EmptyResponseStreak_HandsTheAccountsToRefreshInsteadOfRetryingForever(int accountCount)
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        PathWithAccount[] accounts = new PathWithAccount[accountCount];
        for (int i = 0; i < accountCount; i++)
        {
            accounts[i] = new PathWithAccount(TestItem.ValueKeccaks[i], Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject);
            progressTracker.EnqueueAccountStorage(accounts[i]);
        }

        DrainAccountRangePartition(progressTracker);

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses; attempt++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? batch);
            using (batch)
            {
                Assert.That(batch!.StorageRangeRequest, Is.Not.Null, $"attempt {attempt}: below the streak limit the range is simply offered again");
                Assert.That(batch.StorageRangeRequest!.Accounts.Count, Is.EqualTo(accountCount));

                batch.StorageRangeResponse = CreateEmptySlotsResponse(0);
                Assert.That(snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse), Is.EqualTo(AddRangeResult.ExpiredRootHash));
                snapProvider.ReleaseRequest(batch, responseHandled: true);
            }
        }

        for (int i = 0; i < accountCount; i++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? next);
            using (next)
            {
                Assert.That(next!.AccountsToRefreshRequest, Is.Not.Null,
                    "after the streak the account must be re-proven at the current pivot, not requested as a storage range again");
                Assert.That(accounts.Select(static a => a.Path), Does.Contain(next.AccountsToRefreshRequest!.Paths[0].PathAndAccount!.Path));
                progressTracker.ReportAccountRefreshFinished();
            }
        }

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True, "no storage range may be left queued behind the refreshes");
    }

    // The counter only ever rises, so a promotion has to put it back: otherwise the account stays past the threshold
    // for good and, once a refresh has put its storage back on the queue, every single empty response promotes it
    // again - one refresh per response instead of one per streak, which is what fills the refresh queue.
    [Test]
    public void AddStorageRange_EmptyResponseStreak_StartsOverAfterEachPromotion()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        PathWithAccount account = new(TestItem.ValueKeccaks[0], Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject);
        progressTracker.EnqueueAccountStorage(account);
        DrainAccountRangePartition(progressTracker);

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses; attempt++)
        {
            AnswerNextStorageRangeWithAnEmptyResponse(snapProvider, progressTracker);
        }

        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null, "the streak promotes the account once");
            progressTracker.ReportAccountRefreshFinished();
        }

        // A refresh that finds the account still has storage puts its range back on the queue.
        progressTracker.EnqueueAccountStorage(account);
        AnswerNextStorageRangeWithAnEmptyResponse(snapProvider, progressTracker);

        Assert.That(progressTracker.AccountsToRefreshCount, Is.Zero,
            "one empty response after a promotion must not promote the account again - a fresh streak has to build up first");

        progressTracker.IsFinished(out SnapSyncBatch? retried);
        using (retried)
        {
            Assert.That(retried!.StorageRangeRequest, Is.Not.Null, "the range goes back to the ordinary storage queue");
        }
    }

    // A single-account range is not necessarily a large-storage continuation: DequeStorageToRetrieveRequest builds
    // its batch with StartingHash = Zero, so the tail of the storage queue holding one entry lands on the same
    // branch. Only the continuation carries a non-zero origin, and only it deserves the default-on Warn - an
    // ordinary account whose storage is empty at the pivot must not put one in an operator's log.
    [TestCase(false, TestName = "One-account batch with a zero origin stays on the Debug lane")]
    [TestCase(true, TestName = "Large-storage continuation warns")]
    public void AddStorageRange_EmptyResponseStreak_OnlyWarnsForALargeStorageContinuation(bool isContinuation)
    {
        LaneRecordingLogManager logManager = new();

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .AddSingleton<ILogManager>(logManager) // Put last or it wont work.
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        PathWithAccount account = new(TestItem.ValueKeccaks[0], Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject);

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses; attempt++)
        {
            using StorageRange request = new()
            {
                BlockNumber = 1,
                RootHash = Keccak.EmptyTreeHash,
                Accounts = new[] { account }.ToPooledList(1),
                StartingHash = isContinuation ? TestItem.ValueKeccaks[1] : ValueKeccak.Zero,
                LimitHash = isContinuation ? Keccak.MaxValue.ValueHash256 : null,
            };

            using SlotsAndProofs response = CreateEmptySlotsResponse(0);
            Assert.That(snapProvider.AddStorageRange(request, response), Is.EqualTo(AddRangeResult.ExpiredRootHash));
        }

        string[] promotions = logManager.Warns.Concat(logManager.Debugs)
            .Where(static line => line.Contains("came back empty"))
            .ToArray();

        Assert.That(promotions, Has.Length.EqualTo(1), "the streak must promote the account exactly once");
        Assert.That(logManager.Warns.Any(static line => line.Contains("came back empty")), Is.EqualTo(isContinuation),
            isContinuation
                ? "a stalled continuation is unreachable by any other request and is worth an operator warning"
                : "a one-account batch is an ordinary account and must not warn");
    }

    private sealed class LaneRecordingLogManager : ILogManager, InterfaceLogger
    {
        public List<string> Warns { get; } = [];
        public List<string> Debugs { get; } = [];

        public ILogger GetClassLogger<T>() => new(this);
        public ILogger GetLogger(string loggerName) => new(this);

        public void Warn(string text) => Warns.Add(text);
        public void Debug(string text) => Debugs.Add(text);
        public void Info(string text) { }
        public void Trace(string text) { }
        public void Error(string text, Exception? ex = null) { }

        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsInfo => false;
        public bool IsTrace => false;
        public bool IsError => false;
    }

    /// <summary>
    /// Registers one account as large storage, split over <paramref name="partitionCount"/> partitions the way
    /// <c>Sync.EnableSnapSyncStorageRangeSplit</c> does. Dequeuing a single-account slot range is what registers a
    /// partition, and each distinct limit hash counts as one.
    /// </summary>
    private static void RegisterLargeStorage(ProgressTracker progressTracker, PathWithAccount account, int partitionCount)
    {
        for (int i = 0; i < partitionCount; i++)
        {
            progressTracker.EnqueueNextSlot(new StorageRange
            {
                Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                StartingHash = ValueKeccak.Zero,
                LimitHash = i == partitionCount - 1 ? Keccak.MaxValue : TestItem.Keccaks[i]
            });

            progressTracker.IsFinished(out SnapSyncBatch? slot);
            using (slot)
            {
                Assert.That(slot!.StorageRangeRequest, Is.Not.Null);
            }

            progressTracker.ReportStorageRequestFinished(1);
        }

        Assert.That(progressTracker.LargeStorageProgressCount, Is.EqualTo(1), "guards the premise");
    }

    private static void AnswerNextStorageRangeWithAnEmptyResponse(SnapProvider snapProvider, ProgressTracker progressTracker)
    {
        progressTracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        {
            Assert.That(batch!.StorageRangeRequest, Is.Not.Null);

            batch.StorageRangeResponse = CreateEmptySlotsResponse(0);
            Assert.That(snapProvider.AddStorageRange(batch.StorageRangeRequest!, batch.StorageRangeResponse), Is.EqualTo(AddRangeResult.ExpiredRootHash));
            snapProvider.ReleaseRequest(batch, responseHandled: true);
        }
    }

    // The promotion above is speculative: an empty response is also what a peer that has fallen behind the pivot
    // sends, and under a genuinely stale root every storage response is empty, so one response can push a whole
    // STORAGE_BATCH_SIZE batch over the streak limit at once. ProgressTracker.IsFinished serves the refresh queue
    // ahead of every other request type and a refresh that answers Expired re-queues itself, so an unbounded
    // promotion would leave no slot for account, storage or code work until the pivot moved. Past the cap the
    // account has to go back to the ordinary storage queue instead.
    [Test]
    public void AddStorageRange_EmptyResponseStreak_DoesNotQueueMoreRefreshesThanTheCap()
    {
        const int accountCount = SnapProvider.MaxQueuedEmptyStreakRefreshes + 6;

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        for (int i = 0; i < accountCount; i++)
        {
            progressTracker.EnqueueAccountStorage(new PathWithAccount(TestItem.ValueKeccaks[i], Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject));
        }

        DrainAccountRangePartition(progressTracker);

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses; attempt++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? batch);
            using (batch)
            {
                Assert.That(batch!.StorageRangeRequest, Is.Not.Null, $"attempt {attempt}: the whole batch is offered again");
                Assert.That(batch.StorageRangeRequest!.Accounts.Count, Is.EqualTo(accountCount));

                batch.StorageRangeResponse = CreateEmptySlotsResponse(0);
                Assert.That(snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse), Is.EqualTo(AddRangeResult.ExpiredRootHash));
                snapProvider.ReleaseRequest(batch, responseHandled: true);
            }
        }

        Assert.That(progressTracker.AccountsToRefreshCount, Is.EqualTo(SnapProvider.MaxQueuedEmptyStreakRefreshes),
            "the refresh queue must not grow past the cap, whatever the batch size");

        // The 6 accounts the cap turned away are not lost: they are back on the storage queue, and they get a turn
        // rather than waiting for the whole refresh queue to drain - see MAX_CONSECUTIVE_ACCOUNT_REFRESHES.
        int refreshes = 0;
        int storageAccounts = 0;
        int refreshesBeforeStorageGotATurn = -1;

        for (int guard = 0; guard <= accountCount; guard++)
        {
            if (progressTracker.IsFinished(out SnapSyncBatch? next)) break;

            using (next)
            {
                Assert.That(next, Is.Not.Null, "the queues are not empty yet");
                if (next!.AccountsToRefreshRequest is not null)
                {
                    refreshes++;
                    progressTracker.ReportAccountRefreshFinished();
                }
                else
                {
                    if (refreshesBeforeStorageGotATurn < 0) refreshesBeforeStorageGotATurn = refreshes;
                    storageAccounts += next.StorageRangeRequest!.Accounts.Count;
                    progressTracker.ReportStorageRequestFinished(next.StorageRangeRequest.Accounts.Count);
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshes, Is.EqualTo(SnapProvider.MaxQueuedEmptyStreakRefreshes), "every promoted account is refreshed");
            Assert.That(storageAccounts, Is.EqualTo(6), "and the ones the cap turned away are back on the storage queue");
            Assert.That(refreshesBeforeStorageGotATurn, Is.EqualTo(ProgressTracker.MAX_CONSECUTIVE_ACCOUNT_REFRESHES),
                "the refresh queue must yield to queued storage work instead of draining first");
        }
    }

    // Regression for #13155, second half: when the re-proven account turns out to have no storage at the pivot any
    // more, there is nothing to fetch. Queueing its storage again would only draw the same empty responses.
    [Test]
    public void RefreshAccounts_AccountNoLongerHasStorage_DropsTheStorageRange()
    {
        (ISnapStateServer server, Hash256 root) = BuildSnapServerFromEntries(
        [
            (TestItem.KeccakA, Build.An.Account.WithBalance(1).TestObject),
            (TestItem.KeccakB, Build.An.Account.WithBalance(2).TestObject),
            (TestItem.KeccakC, Build.An.Account.WithBalance(3).TestObject),
        ]);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        // Discovered at an older pivot with storage; the storage has since been emptied.
        PathWithAccount stale = new(TestItem.KeccakA, Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject);
        progressTracker.EnqueueAccountRefresh(stale, ValueKeccak.Zero, Keccak.MaxValue);

        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);
            Assert.That(refresh.AccountsToRefreshRequest!.RootHash, Is.EqualTo(root));

            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                server.GetAccountRanges(root, stale.Path, stale.Path.IncrementPath(), 4000, CancellationToken.None);
            refresh.AccountsToRefreshResponse = new AccountsAndProofs { PathAndAccounts = accounts, Proofs = proofs };

            Assert.That(snapProvider.RefreshAccounts(refresh.AccountsToRefreshRequest, refresh.AccountsToRefreshResponse), Is.EqualTo(AddRangeResult.OK));
            snapProvider.ReleaseRequest(refresh, responseHandled: true);
        }

        Assert.That(stale.Account!.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash), "the proven (empty) storage root is adopted");
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? next), Is.True, "an account without storage leaves nothing to fetch");
        Assert.That(next, Is.Null);
    }

    /// <summary>
    /// The sibling of the test above. A large-storage account that is <em>deleted</em> at the pivot, rather than
    /// merely emptied, is just as terminal: nothing will ever fetch its storage, so nothing else will ever clear its
    /// large-storage entry. Leaving it counts the account in "Large storage left" for the rest of the sync, which is
    /// the exact operator signal #13155 was diagnosed by. Retiring one partition is not enough: under
    /// <c>Sync.EnableSnapSyncStorageRangeSplit</c> the account registers one per split, so the entry has to be dropped
    /// outright.
    /// </summary>
    [TestCase(1, TestName = "{m} (one partition)")]
    [TestCase(2, TestName = "{m} (split into two partitions)")]
    public void RefreshAccounts_AccountNoLongerExists_ClearsItsLargeStorageProgress(int partitionCount)
    {
        (ISnapStateServer server, Hash256 root) = BuildSnapServerFromEntries(
        [
            (TestItem.KeccakA, Build.An.Account.WithBalance(1).TestObject),
            (TestItem.KeccakB, Build.An.Account.WithBalance(2).TestObject),
        ]);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        // A path immediately before an existing account: absent from the state, and its neighbour is what proves
        // the absence. A gap with no successor would come back as an empty range, i.e. Expired rather than NotFound.
        ValueHash256 missing = ((ValueHash256)TestItem.KeccakB).DecrementPath();
        PathWithAccount gone = new(missing, Build.An.Account.WithStorageRoot(TestItem.KeccakG).TestObject);

        RegisterLargeStorage(progressTracker, gone, partitionCount);

        progressTracker.EnqueueAccountRefresh(gone, ValueKeccak.Zero, Keccak.MaxValue);
        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);

            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                server.GetAccountRanges(root, gone.Path, gone.Path.IncrementPath(), 4000, CancellationToken.None);
            refresh.AccountsToRefreshResponse = new AccountsAndProofs { PathAndAccounts = accounts, Proofs = proofs };

            Assert.That(snapProvider.RefreshAccounts(refresh.AccountsToRefreshRequest, refresh.AccountsToRefreshResponse), Is.EqualTo(AddRangeResult.OK));
            snapProvider.ReleaseRequest(refresh, responseHandled: true);
        }

        Assert.That(gone.Account!.StorageRoot, Is.EqualTo(TestItem.KeccakG), "an absent account adopts nothing; guards that this was NotFound and not Verified");
        Assert.That(progressTracker.LargeStorageProgressCount, Is.Zero, "a deleted account must not keep counting as large storage left");
    }

    /// <summary>
    /// The third terminal-for-large-storage outcome. An account that still has storage, but whose refresh resumes it
    /// from origin 0, goes back to the multi-account batching queue - where nothing will ever call
    /// <see cref="ProgressTracker.OnCompletedLargeStorage"/> for it, because that only fires for a single-account
    /// slot range. Its old large-storage entry is obsolete the moment the account restarts at 0, so it has to be
    /// cleared here or it counts towards "Large storage left" for the rest of the sync - however many partitions the
    /// account had been split into.
    /// </summary>
    [TestCase(1, TestName = "{m} (one partition)")]
    [TestCase(2, TestName = "{m} (split into two partitions)")]
    public void RefreshAccounts_AccountRestartsFromOrigin_ClearsItsStaleLargeStorageProgress(int partitionCount)
    {
        Account withStorage = Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakF).TestObject;
        (ISnapStateServer server, Hash256 root) = BuildSnapServerFromEntries(
        [
            (TestItem.KeccakA, withStorage),
            (TestItem.KeccakB, Build.An.Account.WithBalance(2).TestObject),
        ]);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        PathWithAccount account = new(TestItem.KeccakA, withStorage);

        RegisterLargeStorage(progressTracker, account, partitionCount);

        // Refreshed at origin 0, so the Verified arm re-enqueues the whole account rather than a continuation.
        progressTracker.EnqueueAccountRefresh(account, ValueKeccak.Zero, Keccak.MaxValue);
        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);

            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                server.GetAccountRanges(root, account.Path, account.Path.IncrementPath(), 4000, CancellationToken.None);
            refresh.AccountsToRefreshResponse = new AccountsAndProofs { PathAndAccounts = accounts, Proofs = proofs };

            Assert.That(snapProvider.RefreshAccounts(refresh.AccountsToRefreshRequest, refresh.AccountsToRefreshResponse), Is.EqualTo(AddRangeResult.OK));
            snapProvider.ReleaseRequest(refresh, responseHandled: true);
        }

        Assert.That(account.Account!.StorageRoot, Is.EqualTo(TestItem.KeccakF), "guards that this was Verified with storage, not NotFound or emptied");
        Assert.That(progressTracker.LargeStorageProgressCount, Is.Zero,
            "an account restarted at origin 0 must not keep its stale large-storage entry");

        progressTracker.IsFinished(out SnapSyncBatch? requeued);
        using (requeued)
        {
            Assert.That(requeued!.StorageRangeRequest, Is.Not.Null, "and it must still be queued for its storage");
        }
    }

    // The streak counter has to be cleared by a SERVED response too, not only by a promotion: otherwise an account
    // that draws a few empties, is then served normally, and later draws a few more is promoted on a streak that was
    // never consecutive.
    [Test]
    public void AddStorageRange_ServedResponse_EndsTheEmptyStreak()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        PathWithAccount account = new(TestItem.ValueKeccaks[0], Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject);
        progressTracker.EnqueueAccountStorage(account);
        DrainAccountRangePartition(progressTracker);

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses - 1; attempt++)
        {
            AnswerNextStorageRangeWithAnEmptyResponse(snapProvider, progressTracker);
        }

        Assert.That(account.EmptyStorageResponses, Is.EqualTo(SnapProvider.MaxConsecutiveEmptyStorageResponses - 1), "guards the premise");

        progressTracker.IsFinished(out SnapSyncBatch? served);
        using (served)
        {
            Assert.That(served!.StorageRangeRequest, Is.Not.Null);
            served.StorageRangeResponse = CreateEmptySlotsResponse(1);
            snapProvider.AddStorageRange(served.StorageRangeRequest!, served.StorageRangeResponse);
            snapProvider.ReleaseRequest(served, responseHandled: true);
        }

        Assert.That(account.EmptyStorageResponses, Is.Zero, "a served response means the peers are answering this account again");
    }

    [TestCase(nameof(SnapSyncBatch.AccountRangeRequest))]
    [TestCase(nameof(SnapSyncBatch.StorageRangeRequest))]
    [TestCase(nameof(SnapSyncBatch.CodesRequest))]
    public void HandleResponse_ProcessingThrows_OffersTheRequestAgain(string requestKind)
    {
        using IContainer container = CreateContainerBuilder(
                new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 },
                (_) => new TestSnapTrieFactory(
                    static () => throw new IOException("state backend unavailable"),
                    static () => throw new IOException("state backend unavailable")))
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        ISimpleSyncFeed<SnapSyncBatch> feed = container.Resolve<ISimpleSyncFeed<SnapSyncBatch>>();

        ValueHash256 work = TestItem.ValueKeccaks[0];
        switch (requestKind)
        {
            case nameof(SnapSyncBatch.StorageRangeRequest):
                progressTracker.EnqueueAccountStorage(new(work, Account.TotallyEmpty));
                DrainAccountRangePartition(progressTracker);
                break;
            case nameof(SnapSyncBatch.CodesRequest):
                progressTracker.EnqueueCodeHash(work);
                DrainAccountRangePartition(progressTracker);
                break;
        }

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? batch), Is.False);
        ValueHash256? issuedLimit = batch!.AccountRangeRequest?.LimitHash;
        AttachThrowingResponse(batch);

        // The feed disposes the batch itself.
        Assert.That(() => feed.HandleResponse(batch, null), Throws.InstanceOf<IOException>());

        // Assert the same work came back, then consume it so only the active count can keep the phase open.
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? retried), Is.False);
        using (retried)
        {
            switch (requestKind)
            {
                case nameof(SnapSyncBatch.AccountRangeRequest):
                    Assert.That(retried!.AccountRangeRequest?.LimitHash, Is.EqualTo(issuedLimit));
                    progressTracker.UpdateAccountRangePartitionProgress(issuedLimit!.Value, Keccak.MaxValue, false);
                    progressTracker.ReportAccountRangePartitionFinished(issuedLimit.Value);
                    break;
                case nameof(SnapSyncBatch.StorageRangeRequest):
                    Assert.That(retried!.StorageRangeRequest?.Accounts.AsSpan()[0].Path, Is.EqualTo(work));
                    progressTracker.ReportStorageRequestFinished(retried.StorageRangeRequest!.Accounts.Count);
                    break;
                case nameof(SnapSyncBatch.CodesRequest):
                    Assert.That(retried!.CodesRequest?.AsSpan()[0], Is.EqualTo(work));
                    progressTracker.ReportCodeRequestFinished([]);
                    break;
            }
        }

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True,
            "the work was queued again but its active request count was never released");
    }

    private static void AttachThrowingResponse(SnapSyncBatch batch)
    {
        if (batch.AccountRangeRequest is not null)
        {
            batch.AccountRangeResponse = new AccountsAndProofs
            {
                PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.ValueKeccaks[0], Account.TotallyEmpty) },
                Proofs = EmptyByteArrayList.Instance
            };
        }
        else if (batch.StorageRangeRequest is not null)
        {
            batch.StorageRangeResponse = CreateEmptySlotsResponse(batch.StorageRangeRequest.Accounts.Count);
        }
        else if (batch.CodesRequest is not null)
        {
            batch.CodesResponse = new ThrowingByteArrayList();
        }
    }

    private sealed class ThrowingByteArrayList : IByteArrayList
    {
        public int Count => 1;
        public ReadOnlySpan<byte> this[int index] => throw new IOException("code stream unavailable");
        public void Dispose() { }
    }

    [Test]
    public void AddStorageRange_ShouldPersistEntries()
    {
        const int slotCount = 6;
        TestMemDb stateDb = new();
        TestRawTrieStore store = new(stateDb);

        // Build storage tree with RLP-encoded 32-byte values
        Hash256 accountHash = TestItem.Tree.AccountAddress0;
        StorageTree storageTree = new(store.GetTrieStore(accountHash), LimboLogs.Instance);
        PathWithStorageSlot[] slots = new PathWithStorageSlot[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            ValueHash256 slotKey = Keccak.Compute(i.ToBigEndianByteArray());
            byte[] value = (i + 1).ToBigEndianByteArray();
            byte[] rlpValue = Rlp.Encode(value).Bytes;
            storageTree.Set(slotKey, rlpValue, false);
            slots[i] = new PathWithStorageSlot(slotKey, rlpValue);
        }
        storageTree.Commit();
        Array.Sort(slots, (a, b) => a.Path.CompareTo(b.Path));

        StateTree stateTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        stateTree.Set(accountHash, Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject);
        stateTree.Commit();

        // Collect proofs
        AccountProofCollector proofCollector = new(accountHash.Bytes,
            new ValueHash256[] { Keccak.Zero, slots[^1].Path });
        stateTree.Accept(proofCollector, stateTree.RootHash);
        AccountProof proof = proofCollector.BuildResult();

        using IContainer container = CreateContainer();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        StorageRange storageRange = new()
        {
            StartingHash = Keccak.Zero,
            Accounts = new ArrayPoolList<PathWithAccount>(1)
            {
                new(accountHash, new Account(0, 1).WithChangedStorageRoot(storageTree.RootHash))
            },
        };

        Assert.That(snapProvider.AddStorageRangeForAccount(
            storageRange, 0, slots,
            new ByteArrayListAdapter(proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof).ToArray().ToPooledList())), Is.EqualTo(AddRangeResult.OK));
    }

    [Test]
    public void AddAccountRange_SetStartRange_ToAfterLastPath()
    {
        (Hash256, Account)[] entries =
        [
            (TestItem.KeccakA, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakB, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakC, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakD, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakE, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakF, TestItem.GenerateRandomAccount()),
        ];
        Array.Sort(entries, static (e1, e2) => e1.Item1.CompareTo(e2.Item1));

        (ISnapStateServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 1
        })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = ss.GetAccountRanges(
            root, Keccak.Zero, entries[3].Item1, 1.MB, default);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? batch), Is.EqualTo(false));

        using AccountsAndProofs accountsAndProofs = new();
        accountsAndProofs.PathAndAccounts = accounts;
        accountsAndProofs.Proofs = proofs;

        Assert.That(snapProvider.AddAccountRange(batch?.AccountRangeRequest!, accountsAndProofs), Is.EqualTo(AddRangeResult.OK));
        snapProvider.ReleaseRequest(batch!, responseHandled: true);
        Assert.That(progressTracker.IsFinished(out batch), Is.EqualTo(false));
        ValueHash256 startingHash = batch!.AccountRangeRequest!.StartingHash;
        Assert.That(startingHash.CompareTo(entries[3].Item1), Is.GreaterThan(0));
        Assert.That(startingHash.CompareTo(entries[4].Item1), Is.LessThan(0));
    }

    [Test]
    public void AddAccountRange_ShouldNotStoreStorageAfterLimit()
    {
        (Hash256, Account)[] entries =
        [
            (new Hash256("0fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (new Hash256("2fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (new Hash256("7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            // Should split it right here

            (new Hash256("9fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (new Hash256("afffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (new Hash256("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
        ];
        Array.Sort(entries, static (e1, e2) => e1.Item1.CompareTo(e2.Item1));

        (ISnapStateServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 2
        })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = ss.GetAccountRanges(
            root, Keccak.Zero, Keccak.MaxValue, 1.MB, default);

        // The range given out here should be half.
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? batch), Is.EqualTo(false));

        using AccountsAndProofs accountsAndProofs = new();
        accountsAndProofs.PathAndAccounts = accounts;
        accountsAndProofs.Proofs = proofs;

        Assert.That(snapProvider.AddAccountRange(batch?.AccountRangeRequest!, accountsAndProofs), Is.EqualTo(AddRangeResult.OK));

        Assert.That(container.Resolve<ISnapTestHelper>().CountTrieNodes(), Is.EqualTo(3)); // 3 child. Root branch node not saved due to state sync compatibility
    }

    [Test]
    public void Test_EdgeCases([Values("badreq-roothash.zip", "badreq-roothash-2.zip", "badreq-roothash-3.zip", "badreq-trieexception.zip")] string testFileName)
    {
        using DeflateStream decompressor =
            new(
                GetType().Assembly
                    .GetManifestResourceStream($"Nethermind.Synchronization.Test.SnapSync.TestFixtures.{testFileName}")!,
                CompressionMode.Decompress);
        BadReq asReq = JsonSerializer.Deserialize<BadReq>(decompressor)!;
        AccountDecoder acd = new();
        Account[] accounts = new Account[asReq.Accounts.Count];
        for (int i = 0; i < accounts.Length; i++)
        {
            RlpReader context = new(Bytes.FromHexString(asReq.Accounts[i]));
            accounts[i] = acd.Decode(ref context)!;
        }

        ValueHash256[] paths = asReq.Paths.Select((bt) => new ValueHash256(Bytes.FromHexString(bt))).ToArray();
        List<PathWithAccount> pathWithAccounts = accounts.Select((acc, idx) => new PathWithAccount(paths[idx], acc)).ToList();
        List<byte[]> proofs = asReq.Proofs.Select((str) => Bytes.FromHexString(str)).ToList();

        using IContainer container = CreateContainer();
        ISnapTrieFactory factory = container.Resolve<ISnapTrieFactory>();
        Assert.That(SnapProviderHelper.AddAccountRange(
                factory,
                0,
                new ValueHash256(asReq.Root),
                new ValueHash256(asReq.StartingHash),
                new ValueHash256(asReq.LimitHash),
                pathWithAccounts,
                new ByteArrayListAdapter(proofs.ToPooledList())).result, Is.EqualTo(AddRangeResult.OK));
    }

    private record BadReq(
        string Root,
        string StartingHash,
        string LimitHash,
        List<string> Proofs,
        List<string> Paths,
        List<string> Accounts
    );

    private static void DrainAccountRangePartition(ProgressTracker progressTracker)
    {
        progressTracker.IsFinished(out SnapSyncBatch? batch);
        ValueHash256 partitionLimit = batch!.AccountRangeRequest!.LimitHash!.Value;
        progressTracker.UpdateAccountRangePartitionProgress(partitionLimit, Keccak.MaxValue, false);
        progressTracker.ReportAccountRangePartitionFinished(partitionLimit);
        batch.Dispose();
    }

    private static StorageRange CreateStorageRange(int accountCount)
    {
        ArrayPoolList<PathWithAccount> accounts = new(accountCount);
        for (int i = 0; i < accountCount; i++)
        {
            accounts.Add(new PathWithAccount(TestItem.ValueKeccaks[i], Account.TotallyEmpty));
        }

        return new StorageRange { Accounts = accounts, StartingHash = Keccak.Zero };
    }

    private static SlotsAndProofs CreateEmptySlotsResponse(int slotListCount)
    {
        ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> pathsAndSlots = new(slotListCount);
        for (int i = 0; i < slotListCount; i++)
        {
            pathsAndSlots.Add(new ArrayPoolList<PathWithStorageSlot>(0));
        }

        return new SlotsAndProofs { PathsAndSlots = pathsAndSlots, Proofs = EmptyByteArrayList.Instance };
    }

    private static (ISnapStateServer, Hash256) BuildSnapServerFromEntries((Hash256, Account)[] entries)
    {
        SnapshotableMemColumnsDb<FlatDbColumns> columns = new();
        RocksDbPersistence persistence = new(columns, LimboLogs.Instance);
        FlatSnapTrieFactory factory = new(persistence, new TestSyncConfig(), LimboLogs.Instance);
        Hash256 root;

        using (ISnapTree<PathWithAccount> stateTree = factory.CreateStateTree())
        {
            PathWithAccount[] accounts = entries
                .Select(static entry => new PathWithAccount(entry.Item1, entry.Item2))
                .ToArray();
            Array.Sort(accounts, static (left, right) => left.Path.CompareTo(right.Path));
            stateTree.BulkSetAndUpdateRootHash(accounts);
            root = stateTree.RootHash;
            stateTree.Commit(ValueKeccak.MaxValue);
        }

        // Flat snap trees intentionally omit the root node because the sync target receives it from the
        // block header. A serving fixture still needs that node to traverse the state, so write the same
        // root RLP produced by a generic in-memory trie into the flat persistence.
        byte[] rootRlp;
        MemoryNodeStorage rootStorage = new();
        StateTree rootTree = new(new RawScopedTrieStore(rootStorage), LimboLogs.Instance);
        foreach ((Hash256 path, Account account) in entries)
            rootTree.Set(path, account);
        rootTree.Commit();
        root = rootTree.RootHash;
        rootRlp = rootTree.GetNodeByPath([], root)!;

        using (IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
            writeBatch.SetStateTrieNode(TreePath.Empty, rootRlp);

        StateId stateId = new(0, root.ValueHash256);
        IFlatDbManager flatDbManager = Substitute.For<IFlatDbManager>();
        flatDbManager.GatherReadOnlySnapshotBundle(Arg.Any<StateId>())
            .Returns(_ => new ReadOnlySnapshotBundle(
                new SnapshotPooledList(0),
                persistence.CreateReader(),
                recordDetailedMetrics: false,
                PersistedSnapshotStack.Empty()));

        IFlatStateRootIndex stateRootIndex = Substitute.For<IFlatStateRootIndex>();
        stateRootIndex.TryGetStateId(Arg.Any<Hash256>(), out Arg.Any<StateId>())
            .Returns(callInfo =>
            {
                callInfo[1] = stateId;
                return true;
            });

        return (new SnapFlatStateServer(flatDbManager, stateRootIndex, LimboLogs.Instance), root);
    }
}
