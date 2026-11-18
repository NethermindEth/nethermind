// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

public class PivotAncestryBatchTests
{
    public enum UnusableResponse
    {
        NullBatch,
        ShortBatch,
        OversizedBatch,
        BrokenLink,
        Timeout,
    }

    [Test, CancelAfter(30_000)]
    public async Task Ancestry_uses_one_reverse_batch_and_only_fetches_missing_links(
        [Values(0, 1, 64, 65)] int suppliedHeaders, CancellationToken cancellationToken)
    {
        using Context context = new();
        Peer peer = context.AddPeer(suppliedHeaders);

        await context.Updater.EnsureSyncPivot(cancellationToken);

        context.AssertPersistedPivot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.BatchRequests, Is.EqualTo(1));
            Assert.That(peer.SingleRequests, Is.EqualTo(65 - suppliedHeaders));
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task Unusable_ancestry_moves_behind_an_honest_peer(
        [Values] UnusableResponse response, CancellationToken cancellationToken)
    {
        using Context context = new();
        Peer unusable = context.AddPeer(response switch
        {
            UnusableResponse.ShortBatch => 1,
            UnusableResponse.OversizedBatch => 66,
            _ => 65,
        });
        unusable.NullBatch = response == UnusableResponse.NullBatch;
        unusable.MissingSingles = response is UnusableResponse.NullBatch or UnusableResponse.ShortBatch;
        unusable.BrokenIndex = response == UnusableResponse.BrokenLink ? 1 : null;
        unusable.Timeout = response == UnusableResponse.Timeout;

        await context.Updater.EnsureSyncPivot(cancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Metadata.Get(MetadataDbKeys.UpdatedPivotData), Is.Null);
            Assert.That(unusable.BatchRequests, Is.EqualTo(2));
        }
        Peer honest = context.AddPeer(65);
        context.Config.MaxAttemptsToUpdatePivot = 1;

        await context.Updater.EnsureSyncPivot(cancellationToken);

        context.AssertPersistedPivot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unusable.BatchRequests, Is.EqualTo(2));
            Assert.That(unusable.SingleRequests, Is.EqualTo(unusable.MissingSingles ? 2 : 0));
            Assert.That(honest.BatchRequests, Is.EqualTo(1));
            Assert.That(honest.SingleRequests, Is.Zero);
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task A_busy_peer_does_not_end_the_current_attempt([Values] bool busyDuringBatch, CancellationToken cancellationToken)
    {
        using Context context = new();
        Peer busy = context.AddPeer(busyDuringBatch ? 65 : 1);
        busy.BusyDuringBatch = busyDuringBatch;
        busy.BusyDuringSingle = !busyDuringBatch;
        Peer next = context.AddPeer(65);

        await context.Updater.EnsureSyncPivot(cancellationToken);

        context.AssertPersistedPivot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(busy.BatchRequests, Is.EqualTo(1));
            Assert.That(busy.SingleRequests, Is.EqualTo(busyDuringBatch ? 0 : 1));
            Assert.That(next.BatchRequests, Is.EqualTo(1));
        }
    }

    [Test, CancelAfter(15_000)]
    public async Task A_stalling_first_peer_is_not_retried_for_every_ancestor([Values] bool waitForDeadline, CancellationToken cancellationToken)
    {
        ManualTimeProvider clock = new();
        using Context context = new(clock);
        Peer stalled = context.AddPeer(65);
        stalled.Timeout = true;
        stalled.WaitForDeadline = waitForDeadline;
        Peer honest = context.AddPeer(65);

        Task attempt = context.Updater.EnsureSyncPivot(cancellationToken);
        if (waitForDeadline)
        {
            await stalled.DeadlineStarted.Task.WaitAsync(cancellationToken);
            clock.AdvanceAndFireTimer(Timeouts.Eth);
        }
        await attempt.WaitAsync(cancellationToken);

        context.AssertPersistedPivot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stalled.BatchRequests + stalled.SingleRequests, Is.EqualTo(1));
            Assert.That(honest.BatchRequests, Is.EqualTo(1));
            Assert.That(honest.SingleRequests, Is.Zero);
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task A_peer_ignoring_cancellation_cannot_hold_the_pivot_attempt(
        [Values] bool holdBatch, CancellationToken cancellationToken)
    {
        ManualTimeProvider clock = new();
        RecordingTimeProvider recordingClock = new(clock);
        using Context context = new(recordingClock);
        Peer stalled = context.AddPeer(holdBatch ? 65 : 1);
        stalled.IgnoreCancellation = true;
        stalled.HoldBatch = holdBatch;
        stalled.AncestryClock = clock;
        context.AddPeer(65);
        Task attempt = context.Updater.EnsureSyncPivot(cancellationToken);
        try
        {
            await stalled.DeadlineStarted.Task.WaitAsync(cancellationToken);
            Assert.That(recordingClock.DueTimes, Is.EqualTo(new[] { Timeouts.Eth }));
            clock.AdvanceAndFireTimer(holdBatch ? Timeouts.Eth : Timeouts.Eth - TimeSpan.FromSeconds(4));
            await attempt.WaitAsync(cancellationToken);
            context.AssertPersistedPivot();
            Assert.That(stalled.OwnedListDisposals, Is.EqualTo(holdBatch ? 0 : 1));
        }
        finally
        {
            stalled.LateReply.SetResult();
            await attempt.WaitAsync(cancellationToken);
        }

        await stalled.HeadersDisposed.Task.WaitAsync(cancellationToken);
        Assert.That(stalled.OwnedListDisposals, Is.EqualTo(1));
    }

    [Test, CancelAfter(30_000)]
    public async Task Invalid_batch_cannot_persist_a_pivot_and_an_honest_retry_recovers(
        [Values(0, 1, 32, 64)] int brokenIndex, [Values] bool invalidHash, CancellationToken cancellationToken)
    {
        using Context context = new();
        Peer peer = context.AddPeer(65);
        peer.BrokenIndex = brokenIndex;
        peer.InvalidHash = invalidHash;

        await context.Updater.EnsureSyncPivot(cancellationToken);

        Assert.That(context.Metadata.Get(MetadataDbKeys.UpdatedPivotData), Is.Null);
        peer.BrokenIndex = null;
        context.Config.MaxAttemptsToUpdatePivot = 1;
        await context.Updater.EnsureSyncPivot(cancellationToken);
        context.AssertPersistedPivot();
    }

    [Test, CancelAfter(30_000)]
    public async Task Cached_ancestry_needs_no_peer_request(CancellationToken cancellationToken)
    {
        using Context context = new();
        Peer peer = context.AddPeer(65);
        for (ulong number = 35; number <= 99; number++)
            context.Cache.TryAddBlock(context.Source.FindBlock(number, BlockTreeLookupOptions.None)!);

        await context.Updater.EnsureSyncPivot(cancellationToken);

        context.AssertPersistedPivot();
        Assert.That(peer.BatchRequests + peer.SingleRequests, Is.Zero);
    }

    [Test, CancelAfter(30_000)]
    public async Task Cached_ancestry_prefix_requests_only_the_missing_suffix([Values(1, 64)] int cachedHeaders, CancellationToken cancellationToken)
    {
        using Context context = new();
        Peer peer = context.AddPeer(65 - cachedHeaders);
        peer.ExpectedStartNumber = (ulong)(99 - cachedHeaders);
        for (ulong number = peer.ExpectedStartNumber + 1; number <= 99; number++)
            context.Cache.TryAddBlock(context.Source.FindBlock(number, BlockTreeLookupOptions.None)!);

        await context.Updater.EnsureSyncPivot(cancellationToken);

        context.AssertPersistedPivot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.BatchRequests, Is.EqualTo(1));
            Assert.That(peer.SingleRequests, Is.Zero);
        }
    }

    [Test, CancelAfter(30_000)]
    public async Task A_hash_linked_batch_with_a_height_gap_cannot_persist_a_pivot(CancellationToken cancellationToken)
    {
        using Context context = new();
        BlockHeader head = Build.A.BlockHeader.WithNumber(99)
            .WithParentHash(context.Source.FindHeader(97, BlockTreeLookupOptions.None)!.Hash!).TestObject;
        context.Beacon.GetHeadBlockHash().Returns(head.Hash);
        Peer peer = context.AddPeer(65);
        peer.SyncPeer.GetAncestorHeaders(head.Hash!, 65, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            ArrayPoolList<BlockHeader> headers = [with(65), head];
            for (ulong number = 97; number >= 34; number--)
                headers.Add(context.Source.FindHeader(number, BlockTreeLookupOptions.None)!);
            return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(headers);
        });

        await context.Updater.EnsureSyncPivot(cancellationToken);

        Assert.That(context.Metadata.Get(MetadataDbKeys.UpdatedPivotData), Is.Null);
    }

    private sealed class Peer
    {
        public ISyncPeer SyncPeer { get; } = Substitute.For<ISyncPeer>();
        public int BatchRequests { get; private set; }
        public int SingleRequests { get; private set; }
        public bool Timeout { get; set; }
        public bool NullBatch { get; set; }
        public bool MissingSingles { get; set; }
        public bool BusyDuringBatch { get; set; }
        public bool BusyDuringSingle { get; set; }
        public ulong ExpectedStartNumber { get; set; } = 99;
        public TaskCompletionSource DeadlineStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WaitForDeadline { get; set; }
        public bool IgnoreCancellation { get; set; }
        public bool HoldBatch { get; set; }
        public TaskCompletionSource LateReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeadersDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualTimeProvider? AncestryClock { get; set; }
        public int OwnedListDisposals;

        public int? BrokenIndex { get; set; }
        public bool InvalidHash { get; set; }

        public Peer(IBlockTree source, int suppliedHeaders, int index)
        {
            SyncPeer.Node.Returns(new Node(TestItem.PublicKeys[index], "127.0.0.1", 30303 + index));
            SyncPeer.GetAncestorHeaders(Arg.Any<Hash256>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    BatchRequests++;
                    if (BusyDuringBatch) throw new ConcurrencyLimitReachedException("Header queue is full");
                    if (IgnoreCancellation && HoldBatch)
                    {
                        DeadlineStarted.TrySetResult();
                        await LateReply.Task;
                    }
                    if (IgnoreCancellation && !HoldBatch) AncestryClock!.Advance(TimeSpan.FromSeconds(4));
                    if (WaitForDeadline)
                    {
                        DeadlineStarted.TrySetResult();
                        await Task.Delay(System.Threading.Timeout.Infinite, call.Arg<CancellationToken>());
                    }
                    if (Timeout) throw new TimeoutException();
                    if (NullBatch) return null;
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(call.ArgAt<Hash256>(0), Is.EqualTo(source.FindHeader(ExpectedStartNumber, BlockTreeLookupOptions.None)!.Hash));
                        Assert.That(call.ArgAt<int>(1), Is.EqualTo((int)(ExpectedStartNumber - 34)));
                    }
                    ArrayPoolList<BlockHeader> headers = [with(suppliedHeaders)];
                    for (int i = 0; i < suppliedHeaders; i++) headers.Add(Header(source, ExpectedStartNumber - (ulong)i));
                    return (IOwnedReadOnlyList<BlockHeader>?)new TrackedHeaders(headers, () =>
                    {
                        Interlocked.Increment(ref OwnedListDisposals);
                        HeadersDisposed.TrySetResult();
                    });
                });
            SyncPeer.GetHeadBlockHeader(Arg.Any<Hash256?>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                SingleRequests++;
                if (BusyDuringSingle) throw new ConcurrencyLimitReachedException("Header queue is full");
                if (IgnoreCancellation && !HoldBatch)
                {
                    DeadlineStarted.TrySetResult();
                    await LateReply.Task;
                }
                if (WaitForDeadline)
                {
                    DeadlineStarted.TrySetResult();
                    await Task.Delay(System.Threading.Timeout.Infinite, call.Arg<CancellationToken>());
                }
                if (Timeout) throw new TimeoutException();
                if (MissingSingles) return null;
                BlockHeader? requested = source.FindHeader(call.ArgAt<Hash256>(0), BlockTreeLookupOptions.None);
                return requested is null ? null : Header(source, requested.Number);
            });
        }

        private BlockHeader Header(IBlockTree source, ulong number)
        {
            BlockHeader header = source.FindHeader(number, BlockTreeLookupOptions.None)!;
            if (BrokenIndex != (int)(99 - number)) return header;
            BlockHeader broken = Build.A.BlockHeader.WithNumber(number).WithParentHash(header.ParentHash!).WithExtraData([0x42]).TestObject;
            if (InvalidHash) broken.Hash = header.Hash;
            return broken;
        }
    }

    private sealed class TrackedHeaders(IOwnedReadOnlyList<BlockHeader> headers, Action disposed) : IOwnedReadOnlyList<BlockHeader>
    {
        public int Count => headers.Count;
        public BlockHeader this[int index] => headers[index];
        public ReadOnlySpan<BlockHeader> AsSpan() => headers.AsSpan();
        public IEnumerator<BlockHeader> GetEnumerator() => headers.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose()
        {
            headers.Dispose();
            disposed();
        }
    }

    private sealed class RecordingTimeProvider(ManualTimeProvider clock) : TimeProvider
    {
        public ConcurrentQueue<TimeSpan> DueTimes { get; } = new();

        public override long TimestampFrequency => clock.TimestampFrequency;

        public override long GetTimestamp() => clock.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTimes.Enqueue(dueTime);
            return clock.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class Context : IDisposable
    {
        public MemDb Metadata { get; } = new();
        public IBlockTree Source { get; } = Build.A.BlockTree(Build.A.Block.Genesis.TestObject,
            new TestSpecProvider(Cancun.Instance) { TerminalTotalDifficulty = 0 }).WithoutSettingHead.OfChainLength(100).TestObject;
        public BlockCacheService Cache { get; } = new();
        public SyncConfig Config { get; } = new() { FastSync = true, MaxAttemptsToUpdatePivot = 1, MultiSyncModeSelectorLoopTimerMs = 1 };
        public UnsafeStartingSyncPivotUpdater Updater { get; }
        public IBeaconSyncStrategy Beacon { get; } = Substitute.For<IBeaconSyncStrategy>();
        private readonly IBlockTree _target;
        private readonly ISyncPeerPool _pool = Substitute.For<ISyncPeerPool>();
        private readonly List<PeerInfo> _peers = [];

        public Context(TimeProvider? clock = null)
        {
            _target = Build.A.BlockTree().WithMetadataDb(Metadata).TestObject;
            _pool.InitializedPeers.Returns(_peers);
            Beacon.MergeTransitionFinished.Returns(true);
            Beacon.GetHeadBlockHash().Returns(Source.HeadHash);
            ISyncProgressResolver progress = Substitute.For<ISyncProgressResolver>();
            progress.FindBestFullState().Returns(0UL);
            Updater = new(_target, _pool, Config, progress, Cache, Beacon, LimboLogs.Instance, clock ?? TimeProvider.System);
        }

        public Peer AddPeer(int suppliedHeaders)
        {
            Peer peer = new(Source, suppliedHeaders, _peers.Count);
            _peers.Add(new PeerInfo(peer.SyncPeer));
            return peer;
        }

        public void AssertPersistedPivot()
        {
            Hash256 expected = Source.FindHeader(35, BlockTreeLookupOptions.None)!.Hash!;
            IBlockTree restarted = Build.A.BlockTree().WithMetadataDb(Metadata).TestObject;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_target.SyncPivot, Is.EqualTo((35UL, expected)));
                Assert.That(restarted.SyncPivot, Is.EqualTo((35UL, expected)));
            }
        }

        public void Dispose()
        {
            Updater.Dispose();
            Metadata.Dispose();
        }
    }
}
