// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Db;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture, NonParallelizable]
public class SnapRecoveryTests
{
    [Test, CancelAfter(30_000)]
    public async Task Retired_unavailable_replies_stop_after_four_actual_root_sends(
        [Values(0, 1, 2)] int kind, CancellationToken token)
    {
        using Context context = new();
        PeerInfo peer = context.AddPeer(TestItem.PublicKeyA);
        if (kind != 0)
        {
            context.CompleteDiscovery();
            using StorageRange source = ProgressTrackerTests.CreateRequest(1);
            if (kind == 1) context.Tracker.EnqueueAccountStorage(source.Accounts[0]);
            else context.Tracker.EnqueueAccountRefresh(source.Accounts[0], null, null);
        }
        int sends = 0;
        context.Snap.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            sends++;
            context.MovePivot();
            return Task.FromResult(new AccountsAndProofs());
        });
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            sends++;
            context.MovePivot();
            return Task.FromResult(new SlotsAndProofs());
        });
        for (int i = 0; i < 4; i++)
            Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.Emptish));

        Task<SnapSyncBatch?> waiting = context.Feed.PrepareRequest(token);
        if (waiting.IsCompletedSuccessfully)
        {
            SnapSyncBatch? premature = await waiting;
            (premature as IDispatchTerminal)?.CompleteDispatch();
        }
        Assert.That(waiting.IsCompleted, Is.False, "the fifth retired-root request is withheld before any timer wait");
        await context.Clock.TimerCreated.WaitAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sends, Is.EqualTo(4));
            Assert.That(waiting.IsCompleted, Is.False);
            Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
            Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.False);
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
            Assert.That(context.Clock.DueTimes, Does.Contain(TimeSpan.FromMilliseconds(50)));
        }
        context.Stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());

        byte[] code = [1, 2, 3];
        ValueHash256 hash = ValueKeccak.Compute(code);
        context.Snap.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { code })));
        context.Tracker.EnqueueCodeHash(hash);
        SnapSyncBatch? codes = await waiting.WaitAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(codes!.CodesRequest, Is.Not.Null);
            Assert.That(await context.Dispatch(codes, token), Is.EqualTo(SyncResponseHandlingResult.OK));
            Assert.That(context.CodeDb.Get(hash.Bytes), Is.EqualTo(code));
            Assert.That(sends, Is.EqualTo(4));
        }

        Task<SnapSyncBatch?> rollover = context.Feed.PrepareRequest(token);
        Assert.That(rollover.IsCompleted, Is.False);
        context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMinutes(1));
        SnapSyncBatch? resumed = await rollover.WaitAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await context.Dispatch(resumed!, token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
            Assert.That(sends, Is.EqualTo(5));
            Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
            Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.False);
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Sleeping_code_peer_resumes_on_the_single_readiness_timer(
        [Values(0, 1)] int unavailableKind, CancellationToken token)
    {
        using Context context = new();
        context.CompleteDiscovery();
        PeerInfo peer = context.AddPeer(TestItem.PublicKeyA);
        byte[] code = [5, 6, 7];
        ValueHash256 hash = ValueKeccak.Compute(code);
        context.Tracker.EnqueueCodeHash(hash);
        int sends = 0;
        TaskCompletionSource codeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Snap.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            sends++;
            codeEntered.TrySetResult();
            await releaseCode.Task.WaitAsync(token);
            return (IByteArrayList)new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { code });
        });
        SyncPeerAllocation? held = null;
        if (unavailableKind == 0) peer.PutToSleep(AllocationContexts.Snap, DateTime.UtcNow);
        else
        {
            held = new SyncPeerAllocation(AllocationContexts.Snap);
            held.AllocatePeer(peer);
            Assert.That(held.Current, Is.SameAs(peer));
        }
        ObservedFeed observed = new(context.Feed);
        Task completionSignal = context.Tracker.Recovery.ReadinessSignal;
        using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task run = context.CreateDispatcher(observed).Run(runCancellation.Token);
        try
        {
            Assert.That(context.Clock.DueTimes, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(50) }),
                "code-only waiting must register its readiness timer before any watchdog await");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Clock.DueTimes, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(50) }));
                Assert.That(sends, Is.Zero);
                Assert.That(run.IsCompleted, Is.False);
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            }
            for (int tick = 0; tick < 2; tick++)
            {
                Task nextTimer = context.Clock.NextTimerCreated;
                context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(50));
                await nextTimer.WaitAsync(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Clock.ActiveTimers, Is.EqualTo(1));
                    Assert.That(sends, Is.Zero);
                }
            }
            Task processingWait = context.Clock.NextTimerCreated;
            if (held is null) context.Pool.WakeUpAll();
            else held.Dispose();
            context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(50));
            await codeEntered.Task.WaitAsync(token);
            await processingWait.WaitAsync(token);
            Assert.That(context.Clock.ActiveTimers, Is.EqualTo(1));
            releaseCode.TrySetResult();
            await observed.Handled.Task.WaitAsync(token);
            Assert.That(completionSignal.IsCompleted, Is.True, "returning code ownership must wake the prepared wait without another clock tick");
            await run.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sends, Is.EqualTo(1));
                Assert.That(context.CodeDb.Get(hash.Bytes), Is.EqualTo(code));
                Assert.That(context.Tracker.IsSnapGetRangesFinished(), Is.True);
                Assert.That(context.Clock.ActiveTimers, Is.Zero);
                Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
                Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.False);
            }
            context.Stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, 0);
        }
        finally
        {
            releaseCode.TrySetResult();
            held?.Dispose();
            await runCancellation.CancelAsync();
            try { await run.WaitAsync(token); }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested) { }
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Cancelled_dispatch_returns_owned_storage_and_keeps_only_actual_send_charges(
        [Values(0, 1, 2)] int terminalPath, CancellationToken token)

    {
        bool afterResponse = terminalPath == 1;
        using Context context = new();
        context.CompleteDiscovery();
        PeerInfo peer = context.AddPeer(TestItem.PublicKeyA);
        using StorageRange source = ProgressTrackerTests.CreateRequest(1);
        context.Tracker.EnqueueAccountStorage(source.Accounts[0]);
        int sends = 0;
        for (int i = 0; i < 4; i++)
        {
            using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                sends++;
                cancelled.Cancel();
                return Task.FromResult(new SlotsAndProofs());
            });
            ThrowingStrategyFactory startup = new();
            IPeerAllocationStrategyFactory<SnapSyncBatch> factory = terminalPath == 2 ? startup : afterResponse ? context.Strategy :
                new CallbackStrategyFactory(context.Strategy, _ => cancelled.Cancel());
            SimpleDispatcher<SnapSyncBatch> dispatcher = context.CreateDispatcher(context.Feed, factory);
            Task run = dispatcher.Run(cancelled.Token);
            try { await run.WaitAsync(token); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            catch (InvalidOperationException) when (terminalPath == 2) { }
            using (Assert.EnterMultipleScope())
            {
                if (terminalPath == 2)
                {
                    Assert.That(startup.Batch!.ProviderContext, Is.Null);
                    Assert.That(startup.Batch.DispatchEvidence, Is.Null);
                    Assert.That(startup.Batch.RecoveryLease, Is.Null);
                }
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
                Assert.That(peer.IsAllocated(AllocationContexts.Snap), Is.False);
                Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
                Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.False);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sends, Is.EqualTo(afterResponse ? 4 : 0));
            Assert.That(context.Tracker.Recovery.CanDispatch(peer, true, true), Is.EqualTo(!afterResponse));
        }
        context.Stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        SnapSyncBatch? retry = await context.Feed.PrepareRequest(token);
        Assert.That(retry!.StorageRangeRequest!.Accounts.Select(account => account.Path), Is.EqualTo(new[] { source.Accounts[0].Path }));
        ((IDispatchTerminal)retry).CompleteDispatch();
        ((IDispatchTerminal)retry).CompleteDispatch();
        context.Tracker.IsFinished(out SnapSyncBatch? owned);
        Assert.That(owned!.StorageRangeRequest!.Accounts.Select(account => account.Path), Is.EqualTo(new[] { source.Accounts[0].Path }));
        context.Provider.ReleaseRequest(owned, true);
        owned.Dispose();
        ((IDispatchTerminal)owned).CompleteDispatch();
        Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
    }

    [Test, CancelAfter(30_000)]
    public async Task Selection_reservation_race_is_local_and_does_not_report_a_timeout(CancellationToken token)
    {
        using Context context = new();
        context.CompleteDiscovery();
        PeerInfo peer = context.AddPeer(TestItem.PublicKeyA);
        for (int i = 0; i < 3; i++) context.ConsumeRetired(peer);
        using StorageRange source = ProgressTrackerTests.CreateRequest(1);
        context.Tracker.EnqueueAccountStorage(source.Accounts[0]);
        SnapSyncBatch? batch = await context.Feed.PrepareRequest(token);
        CallbackStrategyFactory racing = new(context.Strategy, context.ConsumeRetired);
        Assert.That(await context.Dispatch(batch!, token, racing), Is.EqualTo(SyncResponseHandlingResult.NotAssigned));
        _ = context.Snap.DidNotReceive().GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
            Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.False);
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
        }
        context.Stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
        ISnapSyncPeer secondHandler = Substitute.For<ISnapSyncPeer>();
        PeerInfo second = context.AddPeer(TestItem.PublicKeyB, satellite: secondHandler);
        secondHandler.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new SlotsAndProofs()));
        Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
        _ = secondHandler.Received(1).GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>());
        _ = context.Snap.DidNotReceive().GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>());
        Assert.That(second.HasNoProgress(AllocationContexts.Snap), Is.False);
    }

    [Test, CancelAfter(30_000)]
    // Admission alone takes five minute windows; responses and processing add to that delay.
    public async Task All_changed_accounts_use_nineteen_cohorts_and_five_admission_windows_accepted_residual(
        [Values] bool bogusUnanchoredRoot, CancellationToken token)
    {
        using ServerState server = new(1200);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        server.ChangeStorage();
        context.MovePivot(server.Root);
        PeerInfo peer = context.AddPeer(TestItem.PublicKeyA);
        int unknownSends = 0;
        int refreshSends = 0;
        int anchoredSends = 0;
        HashSet<ValueHash256> committed = [];
        HashSet<ValueHash256> refreshed = [];
        List<int> windows = [];
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            StorageRange range = call.Arg<StorageRange>();
            if (context.Provider.IsUnknown(range))
            {
                unknownSends++;
                windows.Add((int)(context.Clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalMinutes);
                if (bogusUnanchoredRoot)
                    return Task.FromResult(new SlotsAndProofs
                    {
                        PathsAndSlots = range.Accounts.Select(_ => (IOwnedReadOnlyList<PathWithStorageSlot>)
                            new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [3]) }).ToArray().ToPooledList()
                    });
            }
            else
            {
                anchoredSends++;
                foreach (PathWithAccount account in range.Accounts)
                    Assert.That(committed.Add(account.Path), Is.True, "authenticated storage commits once");
            }
            return Task.FromResult(server.Storage(range));
        });
        context.Snap.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            AccountRange range = call.Arg<AccountRange>();
            refreshSends++;
            Assert.That(refreshed.Add(range.StartingHash), Is.True);
            return Task.FromResult(server.AccountsAt(range));
        });
        int turns = 0;
        int consecutiveRefreshes = 0;
        while (!context.Tracker.IsSnapGetRangesFinished())
        {
            Assert.That(++turns, Is.LessThan(1600));
            Task<SnapSyncBatch?> preparing = context.Feed.PrepareRequest(token);
            if (!preparing.IsCompleted)
            {
                await context.Clock.TimerCreated.WaitAsync(token);
                int window = (int)(context.Clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalMinutes;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(unknownSends, Is.EqualTo(4 * (window + 1)));
                    Assert.That(refreshSends, Is.EqualTo(256 * (window + 1)));
                }
                context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMinutes(1));
            }
            SnapSyncBatch? batch = await preparing.WaitAsync(token);
            Assert.That(batch, Is.Not.Null);
            if (batch!.AccountsToRefreshRequest is not null) consecutiveRefreshes++;
            else consecutiveRefreshes = 0;
            Assert.That(consecutiveRefreshes, Is.LessThanOrEqualTo(4), "verified storage remains eligible between refresh turns");
            SyncResponseHandlingResult result = await context.Dispatch(batch, token);
            Assert.That(result, Is.AnyOf(SyncResponseHandlingResult.OK, SyncResponseHandlingResult.Emptish));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unknownSends, Is.EqualTo(19));
            Assert.That(refreshSends, Is.EqualTo(1200));
            Assert.That(anchoredSends, Is.GreaterThan(0));
            Assert.That(committed, Is.EquivalentTo(server.Accounts.Select(account => account.Path)));
            Assert.That(refreshed, Is.EquivalentTo(committed));
            Assert.That(windows, Is.EqualTo(Enumerable.Range(0, 5).SelectMany(window => Enumerable.Repeat(window, window == 4 ? 3 : 4))));
            Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
            Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.False);
        }
        foreach (PathWithAccount account in server.Accounts)
        {
            StorageTree stored = new(new RawScopedTrieStore(context.Container.Resolve<INodeStorage>(), account.Path.ToCommitment()), server.StorageRoot, LimboLogs.Instance);
            Assert.That(stored.Get(ValueKeccak.Zero.Bytes).ToArray(), Is.EqualTo(new byte[] { 2 }));
        }
        TestContext.Out.WriteLine($"Unknown storage sends: {unknownSends}; account proof sends: {refreshSends}; authenticated storage sends: {anchoredSends}; admission windows: 5, plus actual response and verification time.");
    }

    [Test, CancelAfter(30_000)]
    public async Task Refresh_during_evidence_capture_keeps_the_ambiguous_send_charge(CancellationToken token)
    {
        using ServerState server = new(2);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        PeerInfo peer = context.AddPeer(TestItem.PublicKeyA);
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        Hash256 originalRoot = server.Root;
        server.ChangeStorage();
        context.MovePivot(server.Root);
        for (int i = 0; i < 3; i++)
        {
            SnapRecoveryCoordinator.Lease prior = context.Tracker.Recovery.TryReserve(peer, true, 1)!;
            Assert.That(prior.Sent(), Is.True);
            prior.Classify(true, false);
            prior.Complete();
        }

        AddRangeResult? refreshResult = null;
        void RefreshFirstAccount()
        {
            PathWithAccount account = server.Accounts[0];
            using AccountsToRefreshRequest request = new()
            {
                RootHash = server.Root,
                Paths = new ArrayPoolList<AccountWithStorageStartingHash>(1) { new() { PathAndAccount = account } }
            };
            using AccountsAndProofs response = server.AccountsAt(new AccountRange(server.Root, account.Path, account.Path.IncrementPath()));
            refreshResult = context.Provider.RefreshAccounts(request, response);
        }
        PathWithAccount second = server.Accounts[1];
        using (AccountsToRefreshRequest request = new()
        {
            RootHash = server.Root,
            Paths = new ArrayPoolList<AccountWithStorageStartingHash>(1) { new() { PathAndAccount = second } }
        })
        using (AccountsAndProofs response = server.AccountsAt(new AccountRange(server.Root, second.Path, second.Path.IncrementPath())))
            Assert.That(context.Provider.RefreshAccounts(request, response), Is.EqualTo(AddRangeResult.OK));

        using SnapSyncBatch batch = new()
        {
            StorageRangeRequest = new StorageRange
            {
                RootHash = server.Root,
                Accounts = new RefreshingAccountList(server.Accounts, RefreshFirstAccount)
            }
        };
        int sends = 0;
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            sends++;
            return Task.FromResult(server.Storage(call.Arg<StorageRange>()));
        });
        SnapSyncDownloader downloader = new(LimboLogs.Instance, context.Provider, context.Tracker.Recovery);
        await downloader.Dispatch(peer, batch, token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshResult, Is.EqualTo(AddRangeResult.OK));
            Assert.That(batch.DispatchEvidence!.Accounts[0]!.StateRoot, Is.EqualTo((ValueHash256)originalRoot));
            Assert.That(context.Provider.IsUnknown(batch.StorageRangeRequest), Is.False);
            Assert.That(sends, Is.EqualTo(1));
        }
        AddRangeResult result = context.Provider.AddStorageRangeWithResult(batch.StorageRangeRequest, batch.StorageRangeResponse!,
            out _, out bool neutral, out _, batch.DispatchEvidence, batch.RecoveryLease);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AddRangeResult.DifferentRootHash));
            Assert.That(neutral, Is.True);
        }
        batch.RecoveryLease!.Classify(neutral, false);
        ((IDispatchTerminal)batch).CompleteDispatch();

        context.Provider.IsFinished(out SnapSyncBatch? resumed);
        Assert.That(resumed!.StorageRangeRequest, Is.Not.Null);
        using (resumed)
        {
            context.Provider.ReleaseRequest(resumed, false);
        }
        Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
        using SnapSyncBatch fifth = new()
        {
            StorageRangeRequest = new StorageRange
            {
                RootHash = server.Root,
                Accounts = new ArrayPoolList<PathWithAccount>(1) { new(server.Accounts[0].Path, server.Accounts[0].Account) }
            }
        };
        try
        {
            await downloader.Dispatch(peer, fifth, token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(fifth.LocalDeferred, Is.True);
                Assert.That(sends, Is.EqualTo(1));
                Assert.That(context.Tracker.Recovery.CanDispatch(peer, true, false), Is.True);
            }
        }
        finally { ((IDispatchTerminal)fifth).CompleteDispatch(); }
    }

    private sealed class RefreshingAccountList(PathWithAccount[] accounts, Action refresh) : IOwnedReadOnlyList<PathWithAccount>
    {
        private Action? _refresh = refresh;
        public int Count => accounts.Length;
        public PathWithAccount this[int index]
        {
            get
            {
                if (index == 1) Interlocked.Exchange(ref _refresh, null)?.Invoke();
                return accounts[index];
            }
        }
        public ReadOnlySpan<PathWithAccount> AsSpan() => accounts;
        public IEnumerator<PathWithAccount> GetEnumerator() => ((IEnumerable<PathWithAccount>)accounts).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { }
    }

    [Test, CancelAfter(30_000)]
    public async Task Original_dispatch_proof_authority_survives_a_newer_account_proof(
        [Values] bool validOriginal, [Values] bool removedAtNewRoot, [Values] bool continuation, [Values] bool sharedPartitions, CancellationToken token)
    {
        using ServerState server = new(1);
        using Context context = new(server.Root, allocationSlots: sharedPartitions ? 2 : 1);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        SnapSyncBatch? batch = await context.Feed.PrepareRequest(token);
        if (continuation) batch!.StorageRangeRequest!.StartingHash = ValueKeccak.Zero.IncrementPath();
        SnapSyncBatch? sibling = null;
        if (sharedPartitions)
        {
            batch!.StorageRangeRequest!.LimitHash = ValueKeccak.Zero.IncrementPath();
            context.Tracker.EnqueueNextSlot(new StorageRange
            {
                Accounts = new ArrayPoolList<PathWithAccount>(1) { batch.StorageRangeRequest.Accounts[0] },
                StartingHash = ValueKeccak.Zero.IncrementPath().IncrementPath()
            });
            sibling = await context.Feed.PrepareRequest(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sibling!.StorageRangeRequest!.Accounts[0], Is.SameAs(batch.StorageRangeRequest.Accounts[0]));
                Assert.That(sibling.StorageRangeRequest.RootHash, Is.EqualTo(batch.StorageRangeRequest.RootHash));
            }
        }
        int enteredCount = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            StorageRange range = call.Arg<StorageRange>();
            if (Interlocked.Increment(ref enteredCount) == (sharedPartitions ? 2 : 1)) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            if (validOriginal) return server.Storage(range);
            ValueHash256 start = range.StartingHash ?? ValueKeccak.Zero;
            return new SlotsAndProofs
            {
                PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
                {
                    new ArrayPoolList<PathWithStorageSlot>(1) { new(start, [3]) }
                }
            };
        });
        Task<SyncResponseHandlingResult> pending = context.Dispatch(batch!, token);
        Task<SyncResponseHandlingResult>? siblingPending = sibling is null ? null : context.Dispatch(sibling, token);
        try
        {
            await entered.Task.WaitAsync(token);
            Assert.That(batch!.DispatchEvidence!.Accounts[0]!.StateRoot, Is.EqualTo(batch.StorageRangeRequest!.RootHash));
            if (sibling is not null)
                Assert.That(sibling.DispatchEvidence!.Accounts[0]!.StateRoot, Is.EqualTo(sibling.StorageRangeRequest!.RootHash));
            server.ChangeStorage(removedAtNewRoot);
            context.MovePivot(server.Root);
            PathWithAccount account = server.Accounts[0];
            using AccountsToRefreshRequest refresh = new()
            {
                RootHash = server.Root,
                Paths = new ArrayPoolList<AccountWithStorageStartingHash>(1) { new() { PathAndAccount = account, StorageHashLimit = ValueKeccak.MaxValue } }
            };
            using AccountsAndProofs response = server.AccountsAt(new AccountRange(server.Root, account.Path, account.Path.IncrementPath()));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Provider.RefreshAccounts(refresh, response), Is.EqualTo(AddRangeResult.OK));
                Assert.That(context.Provider.IsAnchored(account, server.Root), Is.True);
            }
            release.TrySetResult();
            Assert.That(await pending.WaitAsync(token), Is.EqualTo(validOriginal ? SyncResponseHandlingResult.OK : SyncResponseHandlingResult.NoProgress));
            if (siblingPending is not null)
                Assert.That(await siblingPending.WaitAsync(token), Is.EqualTo(validOriginal ? SyncResponseHandlingResult.OK : SyncResponseHandlingResult.NoProgress));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(batch.DispatchEvidence, Is.Null);
                Assert.That(batch.RecoveryLease, Is.Null);
                if (sibling is not null)
                {
                    Assert.That(sibling.DispatchEvidence, Is.Null);
                    Assert.That(sibling.RecoveryLease, Is.Null);
                }
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
                Assert.That(context.Pool.GetPeer(context.Pool.AllPeers.Single().SyncPeer.Node)!.HasNoProgress(AllocationContexts.Snap), Is.EqualTo(!validOriginal));
            }
            if (validOriginal && continuation)
                context.Stats.DidNotReceive().ReportTransferSpeedEvent(Arg.Any<Node>(), TransferSpeedType.SnapRanges, Arg.Any<long>());
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(token);
            if (siblingPending is not null) await siblingPending.WaitAsync(token);
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Stale_inner_empty_or_inconsistent_storage_is_attributed_and_recovers_at_most_sixty_four_accounts(
        [Values(0, 1, 2)] int shape, CancellationToken token)
    {
        using ServerState server = new(1200);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        Hash256 originalRoot = server.Root;
        server.ChangeStorage();
        context.MovePivot(server.Root);
        Hash256 requestedRoot = server.Root;
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            StorageRange range = call.Arg<StorageRange>();
            ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> lists = [with(range.Accounts.Count)];
            foreach (PathWithAccount account in range.Accounts)
                lists.Add(shape == 0 ? new ArrayPoolList<PathWithStorageSlot>(0) : shape == 1
                    ? new ArrayPoolList<PathWithStorageSlot>(2) { new(ValueKeccak.MaxValue, [2]), new(ValueKeccak.Zero, [2]) }
                    : new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [2]) });
            return Task.FromResult(new SlotsAndProofs
            {
                PathsAndSlots = lists,
                Proofs = shape == 2 ? new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { new byte[] { 0xc1, 0xc0 } }) : EmptyByteArrayList.Instance
            });
        });
        PeerInfo peer = context.Pool.AllPeers.Single();
        for (int attack = 0; attack < 6; attack++)
        {
            if (attack != 0)
            {
                context.MovePivot(requestedRoot);
                foreach (PathWithAccount account in server.Accounts) context.Tracker.EnqueueAccountStorage(account);
            }
            Assert.That(await context.DispatchNext(token),
                Is.EqualTo(attack == 5 ? SyncResponseHandlingResult.LesserQuality : SyncResponseHandlingResult.NoProgress));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.True);
                Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.True);
                Assert.That(context.Tracker.AccountsToRefreshCount, Is.EqualTo(64));
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.EqualTo(64));
            }
            context.Pool.WakeUpAll();
            context.MovePivot(originalRoot);
            HashSet<ValueHash256> recovered = [];
            int handled = 0;
            while (!context.Tracker.IsSnapGetRangesFinished())
            {
                Assert.That(++handled, Is.LessThanOrEqualTo(65));
                context.Tracker.IsFinished(out SnapSyncBatch? next);
                Assert.That(next!.StorageRangeRequest, Is.Not.Null,
                    "authenticated original-root evidence completes every paid refresh locally");
                foreach (PathWithAccount account in next.StorageRangeRequest!.Accounts)
                    Assert.That(recovered.Add(account.Path), Is.True);
                using SlotsAndProofs response = server.Storage(next.StorageRangeRequest);
                Assert.That(context.Provider.AddStorageRange(next.StorageRangeRequest, response), Is.EqualTo(AddRangeResult.OK));
                context.Provider.ReleaseRequest(next, true);
                next.Dispose();
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recovered, Is.EquivalentTo(server.Accounts.Select(account => account.Path)));
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
                Assert.That(context.Tracker.AccountsToRefreshCount, Is.Zero);
            }
        }
        context.Stats.Received(6).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, 0);
        _ = context.Snap.Received(6).GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>());
    }

    [Test, CancelAfter(30_000)]
    public async Task Exhausted_fastest_peer_yields_unknown_storage_but_keeps_authenticated_service(
        [Values] bool publicComposition, CancellationToken token)
    {
        using ServerState server = new(1);
        using Context context = new(server.Root, publicComposition);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        server.ChangeStorage();
        context.MovePivot(server.Root);
        PeerInfo first = context.Pool.AllPeers.Single();
        int firstSends = 0;
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            firstSends++;
            return Task.FromResult(new SlotsAndProofs());
        });
        for (int i = 0; i < 4; i++)
            Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
        Assert.That(context.Tracker.Recovery.CanDispatch(first, true, true), Is.False);

        ISnapSyncPeer slow = Substitute.For<ISnapSyncPeer>();
        int secondSends = 0;
        slow.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            secondSends++;
            return Task.FromResult(new SlotsAndProofs());
        });
        PeerInfo second = context.AddPeer(TestItem.PublicKeyB, 1, slow);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
            Assert.That(firstSends, Is.EqualTo(4));
            Assert.That(secondSends, Is.EqualTo(1));
        }

        second.PutToSleep(AllocationContexts.Snap, DateTime.UtcNow);
        Authenticate(context.Provider, server, server.Accounts[0]);
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            firstSends++;
            return Task.FromResult(server.Storage(call.Arg<StorageRange>()));
        });
        Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.OK));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstSends, Is.EqualTo(5));
            Assert.That(first.HasNoProgress(AllocationContexts.Snap), Is.False);
            Assert.That(first.HasWeakness(AllocationContexts.Snap), Is.False);
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Empty_storage_classification_uses_every_original_account_after_a_refresh_race(
        [Values] bool emptyFirst, [Values] bool otherAnchored, CancellationToken token)
    {
        using ServerState server = new(2);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        server.ChangeStorage();
        PathWithAccount empty = server.Accounts[emptyFirst ? 0 : 1];
        PathWithAccount other = server.Accounts[emptyFirst ? 1 : 0];
        server.RemoveAccount(empty.Path);
        context.MovePivot(server.Root);
        SnapSyncBatch? batch = await context.Feed.PrepareRequest(token);
        Assert.That(batch!.StorageRangeRequest!.Accounts.Count, Is.EqualTo(2));
        Authenticate(context.Provider, server, empty);
        if (otherAnchored) Authenticate(context.Provider, server, other);
        int sends = 0;
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            sends++;
            return Task.FromResult(new SlotsAndProofs());
        });
        Assert.That(await context.Dispatch(batch, token),
            Is.EqualTo(otherAnchored ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.Emptish));
        PeerInfo peer = context.Pool.AllPeers.Single();
        Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.EqualTo(otherAnchored));
        if (!otherAnchored)
        {
            for (int i = 0; i < 3; i++)
                Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sends, Is.EqualTo(4));
                Assert.That(context.Tracker.Recovery.CanDispatch(peer, true, true), Is.False);
            }
        }
    }

    private static void Authenticate(SnapProvider provider, ServerState server, PathWithAccount account)
    {
        using AccountsToRefreshRequest request = new()
        {
            RootHash = server.Root,
            Paths = new ArrayPoolList<AccountWithStorageStartingHash>(1) { new() { PathAndAccount = account, StorageHashLimit = ValueKeccak.MaxValue } }
        };
        using AccountsAndProofs response = server.AccountsAt(new AccountRange(server.Root, account.Path, account.Path.IncrementPath()));
        Assert.That(provider.RefreshAccounts(request, response), Is.EqualTo(AddRangeResult.OK));
    }

    [Test, CancelAfter(30_000)]
    public async Task Processing_wait_and_handler_failure_return_each_response_and_preserve_terminal_ownership(
        [Values(0, 1, 2)] int outcome, CancellationToken token)
    {
        using ServerState server = new(3);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        server.ChangeStorage();
        context.MovePivot(server.Root);
        foreach (PathWithAccount account in server.Accounts) context.Tracker.EnqueueAccountRefresh(account, null, null);
        List<CountedProofs> responses = [];
        TaskCompletionSource twoDownloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Snap.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            AccountsAndProofs response = server.AccountsAt(call.Arg<AccountRange>());
            CountedProofs counted = new(response.Proofs ?? EmptyByteArrayList.Instance);
            response.Proofs = counted;
            lock (responses)
            {
                responses.Add(counted);
                if (responses.Count == 2) twoDownloaded.TrySetResult();
            }
            return Task.FromResult(response);
        });
        using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using BlockingFeed feed = new(context.Feed, outcome == 2);
        Task run = context.CreateDispatcher(feed).Run(runCancellation.Token);
        try
        {
            await feed.Entered.Task.WaitAsync(token);
            context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(50));
            await twoDownloaded.Task.WaitAsync(token);
            context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(50));
            await feed.ThirdPrepared.Task.WaitAsync(token);
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.EqualTo(1));
            if (outcome == 1) await runCancellation.CancelAsync();
            feed.Release.Set();
            try { await run.WaitAsync(token); }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested) { }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
                Assert.That(responses.Count, Is.EqualTo(outcome == 1 ? 2 : 3));
                Assert.That(responses.Select(response => response.DisposeCalls), Is.All.EqualTo(1));
                Assert.That(feed.Batches, Has.Count.EqualTo(3));
                Assert.That(feed.Batches.All(batch => batch.RecoveryLease is null && batch.DispatchEvidence is null && batch.ProviderContext is null), Is.True);
                Assert.That(context.Tracker.AccountsToRefreshCount, Is.EqualTo(outcome == 1 ? 2 : outcome == 2 ? 1 : 0));
            }
            PeerInfo peer = context.Pool.AllPeers.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.IsAllocated(AllocationContexts.Snap), Is.False);
                Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
                Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.False);
            }
            context.Stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, 0);
            for (int i = 0; i < 3; i++) context.ConsumeRetired(peer);
            Assert.That(context.Tracker.Recovery.CanDispatch(peer, true, false), Is.EqualTo(outcome == 0),
                "only sent work whose handling was skipped keeps its originating-window charge");
        }
        finally
        {
            feed.Release.Set();
            await runCancellation.CancelAsync();
            try { await run.WaitAsync(token); }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested) { }
        }
    }

    [Test, CancelAfter(120_000)]
    // Bounded inactive history can admit one early reconnect send; outstanding identities remain pinned.
    public async Task Inactive_identity_history_exposes_the_declared_recycling_cost_and_preserves_pinned_identity_accepted_residual(
        [Values] bool pinned, CancellationToken token)
    {
        using ServerState server = new(4);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        server.ChangeStorage();
        context.MovePivot(server.Root);
        foreach (PathWithAccount account in server.Accounts) context.Tracker.EnqueueAccountRefresh(account, null, null);
        PeerInfo original = context.Pool.AllPeers.Single();
        int sends = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Snap.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            int ordinal = Interlocked.Increment(ref sends);
            context.MovePivot();
            if (pinned && ordinal == 4)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return new AccountsAndProofs();
        });
        for (int i = 0; i < 3; i++) Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
        Task<SyncResponseHandlingResult> fourth = context.DispatchNext(token);
        if (pinned) await entered.Task.WaitAsync(token);
        else Assert.That(await fourth, Is.EqualTo(SyncResponseHandlingResult.Emptish));
        context.Pool.RemovePeer(original.SyncPeer);
        try
        {
            for (int i = 0; i < (pinned ? 4097 : 4096); i++)
            {
                byte[] scalar = new byte[32];
                BinaryPrimitives.WriteInt32BigEndian(scalar.AsSpan(28), 1000 + i);
                using PrivateKey key = new(scalar);
                PeerInfo peer = context.AddPeer(key.PublicKey);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
                    Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
                }
                Assert.That(context.Tracker.Recovery.RetainedIdentities,
                    Is.LessThanOrEqualTo(4096 + context.Pool.InitializedPeersCount + context.Tracker.Recovery.PinnedIdentities));
                context.Pool.RemovePeer(peer.SyncPeer);
            }
            Task<SnapSyncBatch?> waiting = context.Feed.PrepareRequest(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(waiting.IsCompleted, Is.False);
                Assert.That(context.Tracker.Recovery.RetainedIdentities, Is.EqualTo(pinned ? 4097 : 4096));
            }
            PeerInfo reconnected = context.AddPeer(TestItem.PublicKeyA);
            context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(50));
            if (pinned)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(waiting.IsCompleted, Is.False);
                    Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.EqualTo(1));
                    Assert.That(context.Tracker.Recovery.CanDispatch(reconnected, true, false), Is.False);
                }
                release.TrySetResult();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(await fourth.WaitAsync(token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
                    Assert.That(context.Tracker.Recovery.CanDispatch(reconnected, true, false), Is.False);
                }
                Task nextTimer = context.Clock.AdvanceAndObserveNextTimer(TimeSpan.FromMinutes(1));
                context.Clock.AdvanceAndFireDueTimers(TimeSpan.Zero);
                Assert.That(context.Tracker.Recovery.CanDispatch(reconnected, true, false), Is.True);
                // The release may still be canceling its old timer when the manual clock crosses the window.
                await Task.WhenAny(waiting, nextTimer).WaitAsync(token);
                if (!waiting.IsCompleted) context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(50));
            }
            SnapSyncBatch? fresh = await waiting.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await context.Dispatch(fresh!, token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
                Assert.That(sends, Is.EqualTo(pinned ? 4102 : 4101));
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
                Assert.That(reconnected.HasNoProgress(AllocationContexts.Snap), Is.False);
            }
            TestContext.Out.WriteLine(pinned
                ? "The pinned identity retained its exhausted window across reconnect and 4,097 other identities; its next root send followed expiry."
                : "4,097 authenticated identities recycled the earliest inactive identity and admitted exactly one premature root send before minute expiry.");
        }
        finally
        {
            release.TrySetResult();
            await fourth.WaitAsync(token);
        }
    }

    private sealed class CountedProofs(IByteArrayList inner) : IByteArrayList
    {
        internal int DisposeCalls { get; private set; }
        public int Count => inner.Count;
        public ReadOnlySpan<byte> this[int index] => inner[index];
        public void Dispose() { DisposeCalls++; inner.Dispose(); }
    }

    private sealed class BlockingFeed(Synchronization.SnapSync.SnapSyncFeed inner, bool throwFirst)
        : ISimpleSyncFeed<SnapSyncBatch>, IDisposable
    {
        private int _prepared;
        private int _handled;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ThirdPrepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        internal List<SnapSyncBatch> Batches { get; } = [];
        public async Task<SnapSyncBatch?> PrepareRequest(CancellationToken token)
        {
            if (Interlocked.Increment(ref _prepared) > 3) return null;
            SnapSyncBatch? batch = await inner.PrepareRequest(token);
            Batches.Add(batch!);
            if (Batches.Count == 3) ThirdPrepared.TrySetResult();
            return batch;
        }
        public SyncResponseHandlingResult HandleResponse(SnapSyncBatch batch, PeerInfo? peer = null)
        {
            if (peer is not null && Interlocked.Increment(ref _handled) == 1)
            {
                Entered.TrySetResult();
                Release.Wait();
                if (throwFirst) throw new InvalidOperationException("response handling failed before publication");
            }
            return inner.HandleResponse(batch, peer);
        }
        public void Dispose() => Release.Dispose();
    }

    [Test, CancelAfter(30_000)]
    public async Task Alternate_boundary_proof_fault_is_not_neutral_drift([Values] bool missingBoundary, CancellationToken token)
    {
        using ServerState server = new(1);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        context.Tracker.IsFinished(out SnapSyncBatch? initial);
        context.Provider.ReleaseRequest(initial!, true);
        initial!.Dispose();
        ValueHash256[] paths = server.ReplaceWithDenseStorage();
        context.MovePivot(server.Root);
        context.Tracker.EnqueueNextSlot(new StorageRange
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { server.Accounts[0] },
            StartingHash = paths[128],
            LimitHash = paths[383]
        });
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            SlotsAndProofs response = server.Storage(call.Arg<StorageRange>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.PathsAndSlots[0], Has.Count.EqualTo(256));
                Assert.That(response.Proofs!.Count, Is.GreaterThan(1).And.LessThan(256));
            }
            if (missingBoundary)
            {
                byte[] root = response.Proofs[0].ToArray();
                response.Proofs.Dispose();
                response.Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { root });
            }
            return Task.FromResult(response);
        });
        int persistedBefore = context.Container.Resolve<IDbProvider>().StateDb.GetAll().Count();
        Assert.That(await context.DispatchNext(token), Is.EqualTo(missingBoundary ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.Emptish));
        PeerInfo peer = context.Pool.AllPeers.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Container.Resolve<IDbProvider>().StateDb.GetAll().Count(), Is.EqualTo(persistedBefore));
            Assert.That(context.Tracker.AccountsToRefreshCount, Is.EqualTo(1));
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
        }
        if (missingBoundary)
            context.Stats.Received(1).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, 0);
        else
            context.Stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
    }

    [Test, CancelAfter(30_000)]
    public async Task Four_outstanding_root_sends_survive_rollover_and_reconnect_while_code_runs(CancellationToken token)
    {
        using ServerState server = new(8);
        using Context context = new(server.Root, allocationSlots: 4);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        context.Tracker.IsFinished(out SnapSyncBatch? initial);
        context.Provider.ReleaseRequest(initial!, true);
        initial!.Dispose();
        server.ChangeStorage();
        context.MovePivot(server.Root);
        foreach (PathWithAccount account in server.Accounts) context.Tracker.EnqueueAccountRefresh(account, null, null);
        TaskCompletionSource fourSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        context.Snap.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (Interlocked.Increment(ref sends) == 4) fourSent.TrySetResult();
            await release.Task.WaitAsync(token);
            return server.AccountsAt(call.Arg<AccountRange>());
        });
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult(server.Storage(call.Arg<StorageRange>()));
        });
        Task<SyncResponseHandlingResult>[] active = Enumerable.Range(0, 4).Select(_ => context.DispatchNext(token)).ToArray();
        try
        {
            await fourSent.Task.WaitAsync(token);
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.EqualTo(1));
            context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMinutes(1));
            PeerInfo original = context.Pool.AllPeers.Single();
            context.Pool.RemovePeer(original.SyncPeer);
            PeerInfo reconnected = context.AddPeer(TestItem.PublicKeyA);
            Task<SnapSyncBatch?> waiting = context.Feed.PrepareRequest(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(waiting.IsCompleted, Is.False);
                Assert.That(context.Tracker.Recovery.CanDispatch(reconnected, true, false), Is.False);
            }
            byte[] code = [1, 2, 3];
            context.Snap.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { code })));
            context.Tracker.EnqueueCodeHash(ValueKeccak.Compute(code));
            SnapSyncBatch? codeBatch = await waiting.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(codeBatch!.CodesRequest, Is.Not.Null);
                Assert.That(await context.Dispatch(codeBatch, token), Is.EqualTo(SyncResponseHandlingResult.OK));
                Assert.That(context.CodeDb.Get(ValueKeccak.Compute(code).Bytes), Is.EqualTo(code));
                Assert.That(sends, Is.EqualTo(4));
            }
            release.TrySetResult();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await Task.WhenAll(active).WaitAsync(token), Is.All.EqualTo(SyncResponseHandlingResult.OK));
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
                Assert.That(context.Tracker.Recovery.CanDispatch(reconnected, true, false), Is.True);
                Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.OK));
                Assert.That(sends, Is.EqualTo(5));
                Assert.That(reconnected.HasNoProgress(AllocationContexts.Snap), Is.False);
                Assert.That(reconnected.HasWeakness(AllocationContexts.Snap), Is.False);
            }
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(active).WaitAsync(token);
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Replacement_provider_uses_its_declared_storage_result_across_root_changes(
        [Values(0, 1, 2)] int rootChange, [Values(AddRangeResult.OK, AddRangeResult.ExpiredRootHash, AddRangeResult.DifferentRootHash)] AddRangeResult declared,
        CancellationToken token)
    {
        using Context context = new();
        context.CompleteDiscovery();
        using StorageRange source = ProgressTrackerTests.CreateRequest(1);
        context.Tracker.EnqueueAccountStorage(source.Accounts[0]);
        ISnapProvider replacement = Substitute.For<ISnapProvider>();
        replacement.IsFinished(out Arg.Any<SnapSyncBatch?>()).Returns(call =>
        {
            bool finished = context.Provider.IsFinished(out SnapSyncBatch? prepared);
            call[0] = prepared;
            return finished;
        });
        replacement.When(provider => provider.ReleaseRequest(Arg.Any<SnapSyncBatch>(), Arg.Any<bool>(), Arg.Any<bool>()))
            .Do(call => context.Provider.ReleaseRequest(call.ArgAt<SnapSyncBatch>(0), call.ArgAt<bool>(1), call.ArgAt<bool>(2)));
        replacement.AddStorageRange(Arg.Any<StorageRange>(), Arg.Any<SlotsAndProofs>(), out Arg.Any<bool>()).Returns(call =>
        {
            call[2] = declared == AddRangeResult.OK;
            return declared;
        });
        Synchronization.SnapSync.SnapSyncFeed feed = new(context.Stats, replacement, LimboLogs.Instance, context.Tracker.Recovery);
        SnapSyncBatch? batch = await feed.PrepareRequest(token);
        if (rootChange == 1) context.MovePivot();
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (rootChange == 2) context.MovePivot();
            Assert.That(batch!.DispatchEvidence, Is.Null, "replacement authority comes only from its returned result");
            return Task.FromResult(new SlotsAndProofs
            {
                PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1) { ProgressTrackerTests.ValidSlots() }
            });
        });
        OneBatchFeed preparedFeed = new(feed, batch!);
        await context.CreateDispatcher(preparedFeed).Run(token);
        PeerInfo peer = context.Pool.AllPeers.Single();
        using (Assert.EnterMultipleScope())
        {
            bool retiredRoot = rootChange != 0 && declared == AddRangeResult.ExpiredRootHash;
            Assert.That(preparedFeed.Result, Is.EqualTo(declared == AddRangeResult.OK ? SyncResponseHandlingResult.OK : retiredRoot ? SyncResponseHandlingResult.Emptish : SyncResponseHandlingResult.NoProgress));
            Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.EqualTo(!retiredRoot && declared != AddRangeResult.OK));
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
            Assert.That(batch!.RecoveryLease, Is.Null);
            Assert.That(batch.DispatchEvidence, Is.Null);
            Assert.That(batch.ProviderContext, Is.Null);
            Assert.That(context.Tracker.Recovery.CanDispatch(peer, true, true), Is.True);
        }
        replacement.Received(1).AddStorageRange(Arg.Any<StorageRange>(), Arg.Any<SlotsAndProofs>(), out Arg.Any<bool>());
        replacement.Received(1).ReleaseRequest(batch!, true, declared == AddRangeResult.OK);
        if (rootChange != 0 && declared == AddRangeResult.ExpiredRootHash)
            context.Stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
        else
            context.Stats.Received(1).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, declared == AddRangeResult.OK ? 33 / Math.Max(1, batch.ResponseMilliseconds) : 0);
    }

    [Test, CancelAfter(30_000)]
    public async Task Original_window_completion_never_refunds_a_new_window_retired_send(CancellationToken token)
    {
        using ServerState server = new(8);
        using Context context = new(server.Root, allocationSlots: 4);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        context.Tracker.IsFinished(out SnapSyncBatch? initial);
        context.Provider.ReleaseRequest(initial!, true);
        initial!.Dispose();
        server.ChangeStorage();
        context.MovePivot(server.Root);
        foreach (PathWithAccount account in server.Accounts) context.Tracker.EnqueueAccountRefresh(account, null, null);
        TaskCompletionSource[] releases = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        TaskCompletionSource fourSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource fifthSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource fifthRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        context.Snap.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            int ordinal = Interlocked.Increment(ref sends);
            if (ordinal == 4) fourSent.TrySetResult();
            int index = Array.FindIndex(server.Accounts, account => account.Path == call.Arg<AccountRange>().StartingHash);
            Assert.That(index, Is.InRange(0, 3));
            await releases[index].Task.WaitAsync(token);
            return server.AccountsAt(call.Arg<AccountRange>());
        });
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            Assert.That(Interlocked.Increment(ref sends), Is.EqualTo(5));
            context.MovePivot();
            fifthSent.TrySetResult();
            await fifthRelease.Task.WaitAsync(token);
            return new SlotsAndProofs();
        });
        Task<SyncResponseHandlingResult>[] old = Enumerable.Range(0, 4).Select(_ => context.DispatchNext(token)).ToArray();
        Task<SyncResponseHandlingResult>? fresh = null;
        try
        {
            await fourSent.Task.WaitAsync(token);
            context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMinutes(1));
            releases[0].TrySetResult();
            Assert.That(await old[0].WaitAsync(token), Is.EqualTo(SyncResponseHandlingResult.OK));
            fresh = context.DispatchNext(token);
            await fifthSent.Task.WaitAsync(token);
            foreach (TaskCompletionSource release in releases) release.TrySetResult();
            Assert.That(await Task.WhenAll(old).WaitAsync(token), Is.All.EqualTo(SyncResponseHandlingResult.OK));
            fifthRelease.TrySetResult();
            Assert.That(await fresh.WaitAsync(token), Is.EqualTo(SyncResponseHandlingResult.Emptish));
            PeerInfo peer = context.Pool.AllPeers.Single();
            for (int i = 0; i < 3; i++) context.ConsumeRetired(peer);
            Assert.That(context.Tracker.Recovery.CanDispatch(peer, true, false), Is.False,
                "three old-window successes cannot refund the new-window unavailable send");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(sends, Is.EqualTo(5));
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
                Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
            }
        }
        finally
        {
            foreach (TaskCompletionSource release in releases) release.TrySetResult();
            fifthRelease.TrySetResult();
            await Task.WhenAll(old).WaitAsync(token);
            if (fresh is not null) await fresh.WaitAsync(token);
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task A_partial_prepared_cohort_and_a_concurrently_admitted_refresh_both_proceed([Values] bool dispatchStorage, CancellationToken token)
    {
        using ServerState server = new(2);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        context.Tracker.EnqueueAccountStorage(server.Accounts[0]);
        ISnapProvider replacement = Substitute.For<ISnapProvider>();
        bool admitted = false;
        replacement.IsFinished(out Arg.Any<SnapSyncBatch?>()).Returns(call =>
        {
            bool finished = context.Provider.IsFinished(out SnapSyncBatch? prepared);
            call[0] = prepared;
            if (!admitted && prepared?.StorageRangeRequest is not null)
            {
                admitted = true;
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.EqualTo(1));
                ProgressTracker.ResponseAdmission admission = new(context.Tracker.Recovery, null);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Tracker.TryEnqueueAccountRefresh(server.Accounts[1], null, null, admission), Is.True);
                    Assert.That(context.Tracker.Recovery.CohortSlots, Is.EqualTo(2));
                }
            }
            return finished;
        });
        replacement.When(provider => provider.ReleaseRequest(Arg.Any<SnapSyncBatch>(), Arg.Any<bool>(), Arg.Any<bool>()))
            .Do(call => context.Provider.ReleaseRequest(call.ArgAt<SnapSyncBatch>(0), call.ArgAt<bool>(1), call.ArgAt<bool>(2)));
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(server.Storage(call.Arg<StorageRange>())));
        context.Snap.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(server.AccountsAt(call.Arg<AccountRange>())));
        Synchronization.SnapSync.SnapSyncFeed feed = new(context.Stats, replacement, LimboLogs.Instance, context.Tracker.Recovery);
        SnapSyncBatch? storage = null;
        SnapSyncBatch? refresh = null;
        try
        {
            storage = await feed.PrepareRequest(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage?.StorageRangeRequest, Is.Not.Null, "the prepared cohort keeps the slot it reserved and proceeds");
                Assert.That(storage?.RecoveryCohort?.Remaining, Is.EqualTo(1));
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.EqualTo(2));
            }
            refresh = await feed.PrepareRequest(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(refresh?.AccountsToRefreshRequest, Is.Not.Null, "the independently admitted refresh proceeds alongside it");
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.EqualTo(2));
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            }

            SnapSyncBatch prepared = storage!;
            storage = null;
            if (dispatchStorage)
                Assert.That(await context.Dispatch(prepared, token), Is.EqualTo(SyncResponseHandlingResult.OK));
            else
                ((IDispatchTerminal)prepared).CompleteDispatch();
            Assert.That(context.Tracker.Recovery.CohortSlots, Is.EqualTo(1), "the prepared cohort returns its slot on completion or cancellation");

            SnapSyncBatch cancelled = refresh!;
            refresh = null;
            ((IDispatchTerminal)cancelled).CompleteDispatch();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Tracker.AccountsToRefreshCount, Is.EqualTo(1), "a cancelled refresh is still owed");
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.EqualTo(1), "and keeps its slot until it completes");
            }
            Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.OK));
            using (Assert.EnterMultipleScope())
            {
                _ = context.Snap.Received(1).GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>());
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero, "the completed refresh returns its slot");
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            }
        }
        finally
        {
            if (storage is not null) ((IDispatchTerminal)storage).CompleteDispatch();
            if (refresh is not null) ((IDispatchTerminal)refresh).CompleteDispatch();
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Blocked_storage_ticks_leave_full_ranges_and_continuations_uninspected(CancellationToken token)
    {
        using Context context = new();
        context.CompleteDiscovery();
        PeerInfo peer = context.Pool.AllPeers.Single();
        for (int i = 0; i < 4; i++) context.ConsumeRetired(peer);
        using StorageRange source = ProgressTrackerTests.CreateRequest(1200);
        foreach (PathWithAccount account in source.Accounts) context.Tracker.EnqueueAccountStorage(account);
        context.Tracker.EnqueueNextSlot(new StorageRange
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { source.Accounts[0] },
            StartingHash = ValueKeccak.Zero.IncrementPath()
        });
        int inspected = 0;
        context.Tracker.SetAccountEvidence(_ => { inspected++; return null; }, (_, _, _) => { });
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<SnapSyncBatch?> waiting = context.Feed.PrepareRequest(cancellation.Token);
        try
        {
            for (int tick = 0; tick < 3; tick++)
            {
                Task nextTimer = context.Clock.NextTimerCreated;
                context.Clock.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(50));
                await nextTimer.WaitAsync(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(waiting.IsCompleted, Is.False);
                    Assert.That(inspected, Is.Zero, "root-ineligible storage is not dequeued or classified on readiness ticks");
                    Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
                    Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
                }
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await waiting; }
            catch (OperationCanceledException) { }
        }
        Assert.That(context.Clock.ActiveTimers, Is.Zero);
    }

    [Test]
    public void Storage_preparation_caches_peer_eligibility_independently_of_queue_size([Values(1, 1200)] int count)
    {
        using Context context = new();
        context.CompleteDiscovery();
        PeerInfo peer = context.Pool.AllPeers.Single();
        for (int i = 0; i < 4; i++)
        {
            SnapRecoveryCoordinator.Lease lease = context.Tracker.Recovery.TryReserve(peer, true, 1)!;
            Assert.That(lease, Is.Not.Null);
            Assert.That(lease.Sent(), Is.True);
            lease.Classify(true, false);
            lease.Complete();
        }
        using StorageRange source = ProgressTrackerTests.CreateRequest(count);
        foreach (PathWithAccount account in source.Accounts) context.Tracker.EnqueueAccountStorage(account);
        context.Tracker.EnqueueNextSlot(new StorageRange
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { source.Accounts[0] },
            StartingHash = ValueKeccak.Zero.IncrementPath()
        });
        int inspected = 0;
        context.Tracker.SetAccountEvidence(_ => { inspected++; return null; }, (_, _, _) => { });
        int peerSnapshots = 0;
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns(_ => { peerSnapshots++; return context.Pool.InitializedPeers; });
        context.Tracker.Recovery.PeerPool = pool;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            inspected = peerSnapshots = 0;
            Assert.That(context.Tracker.IsFinished(out SnapSyncBatch? batch), Is.False);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(batch, Is.Null);
                // The number of peer snapshots is bounded independently of the storage queue length.
                Assert.That(peerSnapshots, Is.LessThanOrEqualTo(8));
                Assert.That(inspected, Is.EqualTo(count + 1));
                Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero);
                Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            }
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Canonical_storage_proofs_must_agree_with_stale_returned_slots([Values] bool contradictory, CancellationToken token)
    {
        using ServerState server = new(1);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        server.ChangeStorage();
        context.MovePivot(server.Root);
        byte[] proof = ProgressTrackerTests.CreateSingleSlotProof(contradictory ? [1] : [2]);
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(new SlotsAndProofs
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
            {
                new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [2]) }
            },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { proof })
        }));
        int persisted = context.Container.Resolve<IDbProvider>().StateDb.GetAll().Count();
        Assert.That(await context.DispatchNext(token), Is.EqualTo(contradictory ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.Emptish));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Container.Resolve<IDbProvider>().StateDb.GetAll().Count(), Is.EqualTo(persisted));
            Assert.That(context.Tracker.AccountsToRefreshCount, Is.EqualTo(1));
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            Assert.That(context.Pool.AllPeers.Single().HasNoProgress(AllocationContexts.Snap), Is.EqualTo(contradictory));
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task A_peer_cannot_spend_another_peers_pending_refresh_cohort(CancellationToken token)
    {
        using ServerState server = new(128);
        using Context context = new(server.Root);
        context.CompleteDiscovery();
        Assert.That(context.Provider.AddAccountRange(1, server.Root, ValueKeccak.Zero, server.Accounts), Is.EqualTo(AddRangeResult.OK));
        server.ChangeStorage();
        context.MovePivot(server.Root);
        context.Snap.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult(new SlotsAndProofs
        {
            PathsAndSlots = call.Arg<StorageRange>().Accounts.Select(_ => (IOwnedReadOnlyList<PathWithStorageSlot>)
                new ArrayPoolList<PathWithStorageSlot>(0)).ToArray().ToPooledList()
        }));
        Assert.That(await context.DispatchNext(token), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
        PeerInfo first = context.Pool.AllPeers.Single();
        first.PutToSleep(AllocationContexts.Snap, DateTime.UtcNow);
        ISnapSyncPeer secondHandler = Substitute.For<ISnapSyncPeer>();
        PeerInfo second = context.AddPeer(TestItem.PublicKeyB, speed: 10, satellite: secondHandler);
        const int cap = SnapProvider.MaxQueuedEmptyStreakRefreshes;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Tracker.Recovery.CohortSlots, Is.EqualTo(cap));
            Assert.That(context.Tracker.Recovery.CanDispatch(second, true, true), Is.False);
            Assert.That(context.Tracker.Recovery.CanDispatch(second, true, false), Is.True);
        }
        int refreshed = 0;
        int firstCohortRefreshed = 0;
        int samples = 0;
        bool unknownRound = false;
        HashSet<ValueHash256> sentUnknown = [];
        List<(int Accounts, int FirstPending, int Slots)> unknownSends = [];
        context.Stats.When(stats => stats.ReportTransferSpeedEvent(second.SyncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>()))
            .Do(_ => samples++);
        HashSet<ValueHash256> committed = [];
        secondHandler.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            refreshed++;
            // Accounts the second peer never sent as unknown storage are the first peer's promoted refreshes.
            if (!sentUnknown.Contains(call.Arg<AccountRange>().StartingHash)) firstCohortRefreshed++;
            return Task.FromResult(server.AccountsAt(call.Arg<AccountRange>()));
        });
        secondHandler.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            StorageRange range = call.Arg<StorageRange>();
            if (context.Provider.IsUnknown(range))
            {
                unknownRound = true;
                unknownSends.Add((range.Accounts.Count, cap - firstCohortRefreshed, context.Tracker.Recovery.CohortSlots));
                foreach (PathWithAccount account in range.Accounts) sentUnknown.Add(account.Path);
            }
            else
                foreach (PathWithAccount account in range.Accounts)
                    Assert.That(committed.Add(account.Path), Is.True);
            return Task.FromResult(server.Storage(range));
        });
        for (int guard = 0; !context.Tracker.IsSnapGetRangesFinished(); guard++)
        {
            Assert.That(guard, Is.LessThan(180));
            unknownRound = false;
            int samplesBefore = samples;
            Assert.That(await context.DispatchNext(token), Is.AnyOf(SyncResponseHandlingResult.OK, SyncResponseHandlingResult.Emptish));
            if (unknownRound) Assert.That(samples, Is.EqualTo(samplesBefore));
            Assert.That(context.Tracker.Recovery.CohortSlots, Is.LessThanOrEqualTo(cap));
        }
        Assert.That(unknownSends, Is.Not.Empty);
        using (Assert.EnterMultipleScope())
        {
            foreach ((int accounts, int firstPending, int slots) in unknownSends)
            {
                // Every refresh the first peer still owes holds its slot, so the second peer's batch only fits in
                // slots that were free: it was admitted from released capacity, never from the first's reservation.
                Assert.That(slots, Is.GreaterThanOrEqualTo(firstPending + accounts), "the first peer's pending refreshes keep their slots");
                Assert.That(slots, Is.LessThanOrEqualTo(cap));
            }
            Assert.That(unknownSends[0].FirstPending, Is.GreaterThan(0),
                "the second peer proceeds once its batch fits, while the first peer still has refreshes pending");
            Assert.That(sentUnknown, Has.Count.EqualTo(server.Accounts.Length - cap), "the accounts the first cohort could not hold");
            Assert.That(firstCohortRefreshed, Is.EqualTo(cap));
            Assert.That(refreshed, Is.EqualTo(2 * cap));
            Assert.That(committed, Is.EquivalentTo(server.Accounts.Select(account => account.Path)));
            Assert.That(context.Tracker.Recovery.CohortSlots, Is.Zero, "both peers return every slot");
            Assert.That(context.Tracker.Recovery.PinnedIdentities, Is.Zero);
            Assert.That(second.HasNoProgress(AllocationContexts.Snap), Is.False);
            Assert.That(second.HasWeakness(AllocationContexts.Snap), Is.False);
        }
        _ = context.Snap.Received(1).GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>());
        context.Stats.Received(1).ReportTransferSpeedEvent(first.SyncPeer.Node, TransferSpeedType.SnapRanges, 0);
    }

    private sealed class OneBatchFeed(Synchronization.SnapSync.SnapSyncFeed feed, SnapSyncBatch batch) : ISimpleSyncFeed<SnapSyncBatch>
    {
        private SnapSyncBatch? _batch = batch;
        internal SyncResponseHandlingResult Result { get; private set; }
        public Task<SnapSyncBatch?> PrepareRequest(CancellationToken token) => Task.FromResult(Interlocked.Exchange(ref _batch, null));
        public SyncResponseHandlingResult HandleResponse(SnapSyncBatch response, PeerInfo? peer = null) => Result = feed.HandleResponse(response, peer);
    }

    private sealed class ObservedFeed(Synchronization.SnapSync.SnapSyncFeed feed) : ISimpleSyncFeed<SnapSyncBatch>
    {
        internal TaskCompletionSource Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SnapSyncBatch?> PrepareRequest(CancellationToken token) => feed.PrepareRequest(token);
        public SyncResponseHandlingResult HandleResponse(SnapSyncBatch response, PeerInfo? peer = null)
        {
            SyncResponseHandlingResult result = feed.HandleResponse(response, peer);
            Handled.TrySetResult();
            return result;
        }
    }

    private sealed class ThrowingStrategyFactory : IPeerAllocationStrategyFactory<SnapSyncBatch>
    {
        internal SnapSyncBatch? Batch { get; private set; }
        public IPeerAllocationStrategy Create(SnapSyncBatch batch)
        {
            Batch = batch;
            throw new InvalidOperationException("allocation strategy startup failed");
        }
    }

    private sealed class CallbackStrategyFactory(IPeerAllocationStrategyFactory<SnapSyncBatch> inner, Action<PeerInfo> selected)
        : IPeerAllocationStrategyFactory<SnapSyncBatch>
    {
        public IPeerAllocationStrategy Create(SnapSyncBatch batch) => new CallbackStrategy(inner.Create(batch), selected);
        private sealed class CallbackStrategy(IPeerAllocationStrategy inner, Action<PeerInfo> selected) : IPeerAllocationStrategy
        {
            public PeerInfo? Allocate(PeerInfo? current, IEnumerable<PeerInfo> peers, INodeStatsManager stats, IBlockTree tree)
            {
                PeerInfo? peer = inner.Allocate(current, peers, stats, tree);
                if (peer is not null) selected(peer);
                return peer;
            }
        }
    }

    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly ManualTimeProvider _clock = new();
        private readonly Lock _timerCreationLock = new();
        private int _activeTimers;
        internal int ActiveTimers => Volatile.Read(ref _activeTimers);
        internal IReadOnlyList<TimeSpan> DueTimes => _clock.DueTimes;
        internal Task TimerCreated => _clock.TimerCreated;
        internal Task NextTimerCreated => _clock.NextTimerCreated;
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        internal void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);
        internal Task AdvanceAndObserveNextTimer(TimeSpan elapsed)
        {
            lock (_timerCreationLock)
            {
                Task nextTimer = _clock.NextTimerCreated;
                _clock.Advance(elapsed);
                return nextTimer;
            }
        }
        internal void AdvanceAndFireDueTimers(TimeSpan elapsed) => _clock.AdvanceAndFireDueTimers(elapsed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timerCreationLock)
            {
                Interlocked.Increment(ref _activeTimers);
                try { return new ObservedTimer(this, _clock.CreateTimer(callback, state, dueTime, period)); }
                catch { Interlocked.Decrement(ref _activeTimers); throw; }
            }
        }
        private sealed class ObservedTimer(ObservedTimeProvider clock, ITimer inner) : ITimer
        {
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { inner.Dispose(); }
                finally { Interlocked.Decrement(ref clock._activeTimers); }
            }
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }

    private sealed class Context : IDisposable
    {
        internal ObservedTimeProvider Clock { get; } = new();
        internal IContainer Container { get; }
        internal ProgressTracker Tracker { get; }
        internal SnapProvider Provider { get; }
        internal Synchronization.SnapSync.SnapSyncFeed Feed { get; }
        internal SyncPeerPool Pool { get; }
        internal INodeStatsManager Stats { get; } = Substitute.For<INodeStatsManager>();
        internal ISnapSyncPeer Snap { get; } = Substitute.For<ISnapSyncPeer>();
        internal IPeerAllocationStrategyFactory<SnapSyncBatch> Strategy { get; }
        internal SimpleDispatcher<SnapSyncBatch> Dispatcher { get; }
        internal IDb CodeDb => Container.Resolve<IDbProvider>().CodeDb;
        private readonly IBlockTree _tree;
        private readonly ISyncDownloader<SnapSyncBatch> _downloader;
        private ulong _number = 1;

        internal Context(Hash256? root = null, bool publicComposition = false, int allocationSlots = 1)
        {
            Container = new ContainerBuilder()
                .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1, MaxProcessingThreads = 1, AllocationSlots = allocationSlots, EnableSnapDoubleWriteCheck = true }))
                .WithSuggestedHeaderOfStateRoot(root ?? Keccak.EmptyTreeHash)
                .AddSingleton<TimeProvider>(Clock)
                .AddSingleton<INetworkConfig>(new NetworkConfig())
                .AddSingleton<INodeStatsManager>(Stats)
                .Build();
            _tree = Container.Resolve<IBlockTree>();
            _tree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>()).Returns(_tree.BestSuggestedHeader);
            Tracker = Container.Resolve<ProgressTracker>();
            Provider = Container.Resolve<SnapProvider>();
            Feed = publicComposition ? new(Stats, Provider, LimboLogs.Instance)
                : (Synchronization.SnapSync.SnapSyncFeed)Container.Resolve<ISimpleSyncFeed<SnapSyncBatch>>();
            Pool = Container.Resolve<SyncPeerPool>();
            Strategy = publicComposition ? new SnapSyncAllocationStrategyFactory() : Container.Resolve<IPeerAllocationStrategyFactory<SnapSyncBatch>>();
            _downloader = publicComposition ? new SnapSyncDownloader(LimboLogs.Instance) : Container.Resolve<ISyncDownloader<SnapSyncBatch>>();
            Dispatcher = CreateDispatcher(Feed);
            Pool.Start();
            AddPeer(TestItem.PublicKeyA);
        }

        internal PeerInfo AddPeer(PublicKey id, long speed = 100, ISnapSyncPeer? satellite = null)
        {
            foreach (PeerInfo existing in Pool.AllPeers)
                if (existing.SyncPeer.Node.Id.Equals(id)) return existing;
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.Node.Returns(new Node(id, "127.0.0.1", 30303));
            peer.HeadHash = TestItem.KeccakA;
            peer.IsInitialized.Returns(true);
            peer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>()).Returns(call => { call[1] = satellite ?? Snap; return true; });
            NodeStatsLight stats = new(peer.Node);
            stats.AddTransferSpeedCaptureEvent(TransferSpeedType.SnapRanges, speed);
            Stats.GetStatsOrNull(peer.Node).Returns(stats);
            Stats.GetOrAdd(peer.Node).Returns(stats);
            Pool.AddPeer(peer);
            return Pool.GetPeer(peer.Node)!;
        }

        internal void CompleteDiscovery()
        {
            Tracker.IsFinished(out SnapSyncBatch? batch);
            Assert.That(batch?.AccountRangeRequest, Is.Not.Null);
            Tracker.UpdateAccountRangePartitionProgress(ValueKeccak.MaxValue, ValueKeccak.MaxValue, false);
            Provider.ReleaseRequest(batch!, true);
            batch!.Dispose();
        }

        internal void MovePivot(Hash256? root = null)
        {
            _number += 128;
            BlockHeader pivot = Build.A.BlockHeader.WithNumber(_number).WithStateRoot(root ?? Keccak.Compute(BitConverter.GetBytes(_number))).TestObject;
            _tree.BestSuggestedHeader.Returns(pivot);
            _tree.FindHeader(Arg.Any<ulong>()).Returns(pivot);
            Provider.UpdatePivot();
        }

        internal void ConsumeRetired(PeerInfo peer)
        {
            SnapRecoveryCoordinator.Lease lease = Tracker.Recovery.TryReserve(peer, false, 0)!;
            Assert.That(lease, Is.Not.Null);
            Assert.That(lease.Sent(), Is.True);
            lease.Classify(false, true);
            lease.Complete();
        }

        internal SimpleDispatcher<SnapSyncBatch> CreateDispatcher(ISimpleSyncFeed<SnapSyncBatch> feed,
            IPeerAllocationStrategyFactory<SnapSyncBatch>? factory = null) => new(feed,
                _downloader, factory ?? Strategy, AllocationContexts.Snap,
                Pool, Container.Resolve<ISyncConfig>(), LimboLogs.Instance);

        internal async Task<SyncResponseHandlingResult> DispatchNext(CancellationToken token) =>
            await Dispatch((await Feed.PrepareRequest(token))!, token);

        internal async Task<SyncResponseHandlingResult> Dispatch(SnapSyncBatch batch, CancellationToken token,
            IPeerAllocationStrategyFactory<SnapSyncBatch>? factory = null)
        {
            OneBatchFeed prepared = new(Feed, batch);
            await CreateDispatcher(prepared, factory).Run(token);
            return prepared.Result;
        }

        public void Dispose() => Container.Dispose();
    }

    private sealed class ServerState : IDisposable
    {
        private readonly MemDb _db = new();
        private readonly TestRawTrieStore _store;
        private readonly StateTree _state;
        private readonly SnapStateServer _server;
        internal PathWithAccount[] Accounts { get; }
        internal Hash256 Root => _state.RootHash;
        internal Hash256 StorageRoot { get; private set; } = null!;

        internal ServerState(int count)
        {
            _store = new(_db);
            _state = new(_store, LimboLogs.Instance);
            Accounts = new PathWithAccount[count];
            using (_store.BeginBlockCommit(1))
            {
                for (int i = 0; i < count; i++)
                {
                    Hash256 path = Keccak.Compute(BitConverter.GetBytes(i));
                    StorageTree storage = new(_store.GetTrieStore(path), LimboLogs.Instance);
                    storage.Set(ValueKeccak.Zero, [1], false);
                    storage.Commit();
                    StorageRoot = storage.RootHash;
                    Account account = new(0, 1, StorageRoot, Keccak.OfAnEmptyString);
                    Accounts[i] = new(path, account.ToStruct());
                    _state.Set(path, account);
                }
                _state.Commit();
            }
            Array.Sort(Accounts, static (left, right) => left.Path.CompareTo(right.Path));
            _server = new(_store.AsReadOnly(), LimboLogs.Instance);
        }

        internal ValueHash256[] ReplaceWithDenseStorage()
        {
            ValueHash256[] paths = new ValueHash256[512];
            using (_store.BeginBlockCommit(129))
            {
                StorageTree storage = new(_store.GetTrieStore(Accounts[0].Path.ToCommitment()), LimboLogs.Instance);
                for (int i = 0; i < paths.Length; i++)
                {
                    paths[i] = Keccak.Compute(BitConverter.GetBytes(i));
                    storage.Set(paths[i], [2], false);
                }
                storage.Commit();
                StorageRoot = storage.RootHash;
                _state.Set(Accounts[0].Path, new Account(0, 1, StorageRoot, Keccak.OfAnEmptyString));
                _state.Commit();
            }
            Array.Sort(paths);
            return paths;
        }

        internal void ChangeStorage(bool removeAccount = false)
        {
            using (_store.BeginBlockCommit(129))
            {
                foreach (PathWithAccount account in Accounts)
                {
                    StorageTree storage = new(_store.GetTrieStore(account.Path.ToCommitment()), LimboLogs.Instance);
                    storage.Set(ValueKeccak.Zero, [2], false);
                    storage.Commit();
                    StorageRoot = storage.RootHash;
                    _state.Set(account.Path, removeAccount ? null : new Account(0, 1, StorageRoot, Keccak.OfAnEmptyString));
                    if (removeAccount) _state.Set(account.Path.IncrementPath(), new Account(0, 1));
                }
                _state.Commit();
            }
        }

        internal SlotsAndProofs Storage(StorageRange range)
        {
            (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) = _server.GetStorageRanges(
                range.RootHash!, range.Accounts, range.StartingHash, range.LimitHash, 2_000_000, CancellationToken.None);
            return new() { PathsAndSlots = slots, Proofs = proofs };
        }

        internal void RemoveAccount(ValueHash256 path)
        {
            using (_store.BeginBlockCommit(257))
            {
                _state.Set(path, null);
                _state.Set(path.IncrementPath(), new Account(0, 1));
                _state.Commit();
            }
        }

        internal AccountsAndProofs AccountsAt(AccountRange range)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = _server.GetAccountRanges(
                range.RootHash, range.StartingHash, range.LimitHash, 4000, CancellationToken.None);
            return new() { PathAndAccounts = accounts, Proofs = proofs };
        }

        public void Dispose() => _db.Dispose();
    }
}
