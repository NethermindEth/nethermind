// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.V2;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.Test.Mocks;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync.SnapSyncFeed
{
    [TestFixture, NonParallelizable]
    internal class AnalyzeResponsePerPeerTests
    {
        [Test]
        public void Interleaved_bad_peers_are_each_penalized_when_an_honest_peer_progresses(
            [Values(AddRangeResult.InvalidProof, AddRangeResult.InvalidOrder, AddRangeResult.EmptyRange, AddRangeResult.DifferentRootHash)] AddRangeResult failure,
            [Values] bool interleaveCodes)
        {
            PeerInfo firstBad = CreatePeer(TestItem.PublicKeyA);
            PeerInfo secondBad = CreatePeer(TestItem.PublicKeyB);
            PeerInfo honest = CreatePeer(TestItem.PublicKeyC);
            ISnapProvider provider = Substitute.For<ISnapProvider>();
            Synchronization.SnapSync.SnapSyncFeed feed = new(provider, LimboLogs.Instance);
            for (int i = 0; i < AllowedInvalidResponses; i++)
            {
                feed.AnalyzeResponsePerPeer(failure, firstBad);
                feed.AnalyzeResponsePerPeer(failure, secondBad);
                feed.AnalyzeResponsePerPeer(AddRangeResult.OK, honest);
                if (interleaveCodes)
                {
                    feed.AnalyzeResponsePerPeer(AddRangeResult.OK, firstBad, isRangeResult: false);
                    feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, secondBad, isRangeResult: false);
                }
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(feed.AnalyzeResponsePerPeer(failure, firstBad), Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
                Assert.That(feed.AnalyzeResponsePerPeer(failure, secondBad), Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            }
            provider.DidNotReceive().UpdatePivot();
        }

        [Test]
        public void Committed_range_progress_resets_the_stall_and_preserves_the_peers_fault_history()
        {
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            Synchronization.SnapSync.SnapSyncFeed feed = new(Substitute.For<ISnapProvider>(), LimboLogs.Instance);
            for (int i = 0; i < AllowedInvalidResponses; i++)
                feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer);

            SyncResponseHandlingResult result = feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer,
                isRangeResult: true, unavailable: false, productiveRange: true);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
                Assert.That(Metrics.SnapConsecutiveUnproductiveResponses, Is.Zero);
            }
        }

        [Test]
        public void Test01()
        {
            PeerInfo peer1 = CreatePeer(TestItem.PublicKeyA);
            PeerInfo peer2 = CreatePeer(TestItem.PublicKeyB);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);

            SyncResponseHandlingResult result = feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);

            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            result = feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
        }

        [Test]
        public void Test02()
        {
            PeerInfo peer1 = CreatePeer(TestItem.PublicKeyA);
            PeerInfo peer2 = CreatePeer(TestItem.PublicKeyB);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);

            SyncResponseHandlingResult result = feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);

            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            result = feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, peer1);
            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.NoProgress));
        }

        [Test]
        public void Test03()
        {
            using RootFeedContext context = new();
            PeerInfo first = CreatePeer(TestItem.PublicKeyA);
            PeerInfo second = CreatePeer(TestItem.PublicKeyB);
            for (int i = 0; i < 8; i++)
                Assert.That(context.Unavailable(i % 2 == 0 ? first : second), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            context.Pivot.Received(1).UpdateHeaderForcefully();
        }

        [Test]
        public void Test04()
        {
            PeerInfo peer1 = CreatePeer(TestItem.PublicKeyA);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            for (int i = 0; i < 200; i++)
            {
                feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            }
        }

        [Test]
        public void Single_peer_with_consecutive_failures_reports_no_progress()
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 6; i++)
                Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            context.Pivot.Received(1).UpdateHeaderForcefully();
        }

        // When a single peer has produced a recent success, a brief failure burst must still
        // be tolerated and not trigger a pivot refresh on the first failure threshold breach.
        [Test]
        public void Single_peer_with_recent_success_is_not_punished_below_threshold(
            [Values(AddRangeResult.InvalidProof, AddRangeResult.InvalidOrder, AddRangeResult.EmptyRange, AddRangeResult.OutOfBounds)] AddRangeResult failure)
        {
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer);
            for (int i = 0; i < AllowedInvalidResponses; i++)
            {
                SyncResponseHandlingResult result = feed.AnalyzeResponsePerPeer(failure, peer);
                Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            }

            snapProvider.DidNotReceive().UpdatePivot();
        }

        // Regression: a freshly added peer that fails its first AllowedInvalidResponses
        // requests must still be punished when the log contains entries from other,
        // healthy peers - even if those entries sit further back than the newcomer's
        // recent failures.
        [Test]
        public void New_peer_failing_burst_is_punished_when_log_holds_other_healthy_peers()
        {
            PeerInfo healthyPeer = CreatePeer(TestItem.PublicKeyA);
            PeerInfo newPeer = CreatePeer(TestItem.PublicKeyB);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            for (int i = 0; i < AllowedInvalidResponses; i++)
            {
                feed.AnalyzeResponsePerPeer(AddRangeResult.OK, healthyPeer);
            }

            SyncResponseHandlingResult? lastResult = null;
            for (int i = 0; i <= AllowedInvalidResponses; i++)
            {
                lastResult = feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, newPeer);
            }

            Assert.That(lastResult, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            snapProvider.DidNotReceive().UpdatePivot();
        }

        [Test]
        public void Punishes_the_only_peer_once_it_keeps_failing_across_a_pivot_update()
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 11; i++)
                Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            context.Pivot.Received(2).UpdateHeaderForcefully();
            Assert.That(context.Verified(peer), Is.EqualTo(SyncResponseHandlingResult.OK));
            for (int i = 0; i < 6; i++)
                Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
        }

        [Test]
        public void Does_not_punish_a_different_peer_for_the_previous_peers_pivot_update()
        {
            using RootFeedContext context = new();
            PeerInfo first = CreatePeer(TestItem.PublicKeyA);
            PeerInfo second = CreatePeer(TestItem.PublicKeyB);
            for (int i = 0; i < 6; i++) context.Unavailable(first);
            for (int i = 0; i < 6; i++)
                Assert.That(context.Unavailable(second), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            context.Pivot.Received(2).UpdateHeaderForcefully();
        }

        [Test]
        public void Punishes_the_reconnected_peer_that_keeps_failing_across_a_pivot_update()
        {
            using RootFeedContext context = new();
            PeerInfo first = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 6; i++) context.Unavailable(first);
            PeerInfo reconnected = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 5; i++) context.Unavailable(reconnected);
            Assert.That(context.Unavailable(reconnected), Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            context.Pivot.Received(2).UpdateHeaderForcefully();
        }

        [Test]
        public void Code_response_does_not_clear_the_repeat_offender_guard()
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 6; i++) context.Unavailable(peer);
            Assert.That(context.Code(peer), Is.EqualTo(SyncResponseHandlingResult.OK));
            for (int i = 0; i < 5; i++) context.Unavailable(peer);
            Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            context.Pivot.Received(2).UpdateHeaderForcefully();
        }

        [Test]
        public void Punishes_the_offender_even_when_the_pivot_never_moves()
        {
            using RootFeedContext context = new();
            context.Pivot.Diff.Returns(0UL);
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 11; i++)
                Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
        }

        [Test]
        public void Public_analyzer_without_root_evidence_never_updates_pivot(
            [Values(AddRangeResult.DifferentRootHash, AddRangeResult.MissingRootHashInProofs, AddRangeResult.ExpiredRootHash)] AddRangeResult failure)
        {
            ISnapProvider provider = Substitute.For<ISnapProvider>();
            Synchronization.SnapSync.SnapSyncFeed feed = new(provider, LimboLogs.Instance);
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 5; i++)
                Assert.That(feed.AnalyzeResponsePerPeer(failure, peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            Assert.That(feed.AnalyzeResponsePerPeer(failure, peer), Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            provider.DidNotReceive().UpdatePivot();
        }

        [Test]
        public void Peers_without_node_ids_retain_only_the_same_instance_history()
        {
            using RootFeedContext context = new();
            PeerInfo first = new(Substitute.For<ISyncPeer>());
            PeerInfo second = new(Substitute.For<ISyncPeer>());
            for (int i = 0; i < 5; i++)
                Assert.That(context.Feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, first), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, second), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
                Assert.That(context.Feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, first), Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            }
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
        }

        [Test]
        public void Productive_old_root_bytes_preserve_current_root_unavailability()
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            Hash256 oldRoot = context.Root;
            context.ChangeRoot(TestItem.KeccakF);
            for (int i = 0; i < 5; i++) context.Unavailable(peer);
            Assert.That(context.Verified(peer, oldRoot), Is.EqualTo(SyncResponseHandlingResult.OK));
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
            Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            context.Pivot.Received(1).UpdateHeaderForcefully();
        }

        [Test]
        public void Productive_mixed_storage_resets_unavailability_only_at_current_root([Values] bool currentRoot)
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            SnapSyncBatch mixed = context.PrepareProductiveMixedStorage(currentRoot);
            for (int i = 0; i < 5; i++)
                Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            context.Stats.ClearReceivedCalls();
            Assert.That(context.Feed.HandleResponse(mixed, peer), Is.EqualTo(SyncResponseHandlingResult.Emptish));
            context.Stats.Received(1).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, 33);
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
            SnapSyncBatch pendingRefresh = context.DeferStorageRefresh();
            try
            {
                Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
                context.Pivot.Received(currentRoot ? 0 : 1).UpdateHeaderForcefully();
                if (currentRoot)
                {
                    for (int i = 0; i < 4; i++)
                        Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
                    context.Pivot.DidNotReceive().UpdateHeaderForcefully();
                    Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
                    context.Pivot.Received(1).UpdateHeaderForcefully();
                }
            }
            finally
            {
                context.Feed.HandleResponse(pendingRefresh);
            }
        }

        [Test]
        public void Code_progress_preserves_accumulated_root_unavailability()
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 5; i++) context.Unavailable(peer);
            Assert.That(context.Code(peer), Is.EqualTo(SyncResponseHandlingResult.OK));
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
            Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            context.Pivot.Received(1).UpdateHeaderForcefully();
        }

        [Test]
        public void Current_root_unavailability_interleaved_with_faults_attempts_pivot_at_six()
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 5; i++) context.Unavailable(peer);
            Assert.That(context.Forged(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
            Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            context.Pivot.Received(1).UpdateHeaderForcefully();
        }

        [Test]
        public void Never_newer_head_preserves_accumulated_evidence_and_exact_attempt_cadence()
        {
            using RootFeedContext context = new();
            context.Pivot.Diff.Returns(0UL);
            long suppressed = Metrics.ForcedStatePivotUpdatesSuppressed;
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 1; i <= 24; i++)
            {
                context.Unavailable(peer);
                Assert.That(Metrics.ForcedStatePivotUpdatesSuppressed - suppressed, Is.EqualTo(i / 6));
            }
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
        }

        [Test]
        public void Head_becomes_eligible_after_suppressed_attempt()
        {
            using RootFeedContext context = new();
            context.Pivot.Diff.Returns(0UL);
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 6; i++) context.Unavailable(peer);
            context.Pivot.Diff.Returns(64UL);
            for (int i = 0; i < 5; i++) context.Unavailable(peer);
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
            context.Unavailable(peer);
            context.Pivot.Received(1).UpdateHeaderForcefully();
        }

        [Test]
        public void Old_root_unavailability_after_pivot_is_neutral()
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            Hash256 oldRoot = context.Root;
            for (int i = 0; i < 5; i++) context.Unavailable(peer);
            context.ChangeRoot(TestItem.KeccakF);
            context.Stats.ClearReceivedCalls();
            for (int i = 0; i < 12; i++)
                Assert.That(context.Unavailable(peer, oldRoot), Is.EqualTo(SyncResponseHandlingResult.Emptish));
            context.Stats.DidNotReceive().ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
            Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
        }

        [Test]
        public void Current_root_verified_range_resets_unavailability()
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 5; i++) context.Unavailable(peer);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Verified(peer), Is.EqualTo(SyncResponseHandlingResult.OK));
                Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            }
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
        }

        [Test]
        public void Forged_current_root_response_never_progresses()
        {
            using RootFeedContext context = new();
            PeerInfo peer = CreatePeer(TestItem.PublicKeyA);
            for (int i = 0; i < 5; i++)
                Assert.That(context.Forged(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            Assert.That(context.Forged(CreatePeer(TestItem.PublicKeyA)), Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            context.Pivot.DidNotReceive().UpdateHeaderForcefully();
            context.Stats.Received(6).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, 0);
        }

        [Test]
        public void Rotating_unavailable_peers_trigger_one_guarded_pivot()
        {
            using RootFeedContext context = new();
            for (int i = 0; i < 11; i++)
            {
                PeerInfo peer = CreatePeer(TestItem.PublicKeys[i]);
                Assert.That(context.Unavailable(peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            }
            context.Pivot.Received(1).UpdateHeaderForcefully();
        }

        private sealed class RootFeedContext : IDisposable
        {
            private readonly IContainer _container;
            private readonly ConcurrentHashSet<Hash256> _updatedStorages = [];
            private readonly PathWithAccount _account = new(ValueKeccak.Zero, Account.TotallyEmpty.ToStruct());
            private readonly ProgressTracker _tracker;
            public IStateSyncPivot Pivot { get; } = Substitute.For<IStateSyncPivot>();
            public INodeStatsManager Stats { get; } = Substitute.For<INodeStatsManager>();
            public Hash256 Root { get; private set; }
            public Synchronization.SnapSync.SnapSyncFeed Feed { get; }

            public RootFeedContext()
            {
                using MemDb db = new();
                StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
                tree.Set(_account.Path, _account.Account.ToAccount());
                tree.UpdateRootHash();
                Root = tree.RootHash;
                Pivot.UpdatedStorages.Returns(_updatedStorages);
                Pivot.Diff.Returns(64UL);
                ChangeRoot(Root);
                _container = new ContainerBuilder()
                    .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
                    .AddSingleton(Pivot).Build();
                _tracker = _container.Resolve<ProgressTracker>();
                Feed = new(Stats, _container.Resolve<SnapProvider>(), LimboLogs.Instance);
            }

            public void ChangeRoot(Hash256 root)
            {
                Root = root;
                Pivot.GetPivotHeader().Returns(Build.A.BlockHeader.WithNumber(1).WithStateRoot(root).TestObject);
            }

            public SyncResponseHandlingResult Unavailable(PeerInfo peer, Hash256? requestedRoot = null)
            {
                _tracker.IsFinished(out SnapSyncBatch? batch);
                AccountRange original = batch!.AccountRangeRequest!;
                batch.AccountRangeRequest = new(requestedRoot ?? Root, original.StartingHash, original.LimitHash, original.BlockNumber);
                batch.AccountRangeResponse = new();
                return Feed.HandleResponse(batch, peer);
            }

            public SyncResponseHandlingResult Forged(PeerInfo peer)
            {
                _tracker.IsFinished(out SnapSyncBatch? batch);
                batch!.AccountRangeResponse = new()
                {
                    PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(_account.Path, new Account(1, 1).ToStruct()) }
                };
                return Feed.HandleResponse(batch, peer);
            }

            public SyncResponseHandlingResult Verified(PeerInfo peer, Hash256? requestedRoot = null)
            {
                _tracker.IsFinished(out SnapSyncBatch? batch);
                AccountRange original = batch!.AccountRangeRequest!;
                batch.AccountRangeRequest = new(requestedRoot ?? Root, original.StartingHash, original.LimitHash, original.BlockNumber);
                batch.AccountRangeResponse = new() { PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { _account } };
                SyncResponseHandlingResult result = Feed.HandleResponse(batch, peer);
                _tracker.Reset();
                return result;
            }

            public SyncResponseHandlingResult Code(PeerInfo peer)
            {
                byte[] code = [1, 2, 3];
                _tracker.IsFinished(out SnapSyncBatch? accountBatch);
                _tracker.UpdateAccountRangePartitionProgress(ValueKeccak.MaxValue, ValueKeccak.MaxValue, false);
                _container.Resolve<SnapProvider>().ReleaseRequest(accountBatch!, true);
                accountBatch!.Dispose();
                _tracker.EnqueueCodeHash(ValueKeccak.Compute(code));
                _tracker.IsFinished(out SnapSyncBatch? batch);
                batch!.CodesResponse = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { code });
                SyncResponseHandlingResult result = Feed.HandleResponse(batch, peer);
                _tracker.Reset();
                return result;
            }

            public SnapSyncBatch PrepareProductiveMixedStorage(bool currentRootBytes)
            {
                SnapProvider provider = _container.Resolve<SnapProvider>();
                using StorageRange accounts = ProgressTrackerTests.CreateRequest(2);
                using MemDb db = new();
                StateTree state = new(new RawScopedTrieStore(db), LimboLogs.Instance);
                state.Set(accounts.Accounts[0].Path, accounts.Accounts[0].Account.ToAccount());
                ProgressTrackerTests.VerifyAccountEvidence(provider, accounts.Accounts[1]);
                state.UpdateRootHash();
                Hash256 currentRoot = state.RootHash;
                ChangeRoot(currentRootBytes ? currentRoot : TestItem.KeccakF);
                ProgressTrackerTests.CompleteAccountPartition(_tracker, provider);
                foreach (PathWithAccount account in accounts.Accounts) _tracker.EnqueueAccountStorage(account);
                _tracker.IsFinished(out SnapSyncBatch? batch);
                Assert.That(ProgressTrackerTests.VerifyAccountEvidence(provider, accounts.Accounts[0]), Is.EqualTo(currentRoot));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(batch!.StorageRangeRequest!.Accounts.Count, Is.EqualTo(2));
                    Assert.That(batch.StorageRangeRequest.RootHash, Is.EqualTo(Root));
                }
                batch.StorageRangeResponse = new()
                {
                    PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(2)
                    {
                        ProgressTrackerTests.ValidSlots(),
                        new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [2]) }
                    }
                };
                _tracker.Reset();
                return batch;
            }

            public SnapSyncBatch DeferStorageRefresh()
            {
                _tracker.IsFinished(out SnapSyncBatch? batch);
                Assert.That(batch!.AccountsToRefreshRequest, Is.Not.Null);
                return batch;
            }

            public void Dispose() => _container.Dispose();
        }

        private const int AllowedInvalidResponses = Synchronization.SnapSync.SnapSyncFeed.AllowedInvalidResponses;

        private static PeerInfo CreatePeer(PublicKey nodeId)
        {
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(nodeId, "127.0.0.1", 30303));
            return new PeerInfo(syncPeer);
        }

        public enum Fault { None, InvalidOrder, InvalidProof }

        [Test, CancelAfter(30_000)]
        public async Task Stale_storage_ambiguity_preserves_peer_evidence_without_pivot_recovery(
            [Values(0, 1, 2)] int responseShape, [Values(1, 1200)] int accountCount, CancellationToken cancellationToken)
        {
            using MemDb db = new();
            StateTree state = new(new RawScopedTrieStore(db), LimboLogs.Instance);
            using StorageRange source = ProgressTrackerTests.CreateRequest(accountCount);
            foreach (PathWithAccount account in source.Accounts) state.Set(account.Path, account.Account.ToAccount());
            state.UpdateRootHash();
            Hash256 oldRoot = state.RootHash;
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
                .WithSuggestedHeaderOfStateRoot(oldRoot).Build();
            ProgressTracker tracker = container.Resolve<ProgressTracker>();
            SnapProvider provider = container.Resolve<SnapProvider>();
            tracker.IsFinished(out SnapSyncBatch? discovered);
            Assert.That(provider.AddAccountRange(1, oldRoot, ValueKeccak.Zero, source.Accounts), Is.EqualTo(AddRangeResult.OK));
            provider.ReleaseRequest(discovered!, true);
            discovered!.Dispose();
            IBlockTree blockTree = container.Resolve<IBlockTree>();
            BlockHeader current = Build.A.BlockHeader.WithNumber(129).WithStateRoot(TestItem.KeccakF).TestObject;
            blockTree.BestSuggestedHeader.Returns(current);
            blockTree.FindHeader(Arg.Any<ulong>()).Returns(current);
            provider.UpdatePivot();
            tracker.IsFinished(out SnapSyncBatch? storage);
            ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> lists = [with(accountCount)];
            if (responseShape != 0)
                foreach (PathWithAccount account in source.Accounts)
                    lists.Add(responseShape == 2 ? new ArrayPoolList<PathWithStorageSlot>(0) :
                        new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [2]) });
            storage!.StorageRangeResponse = new() { PathsAndSlots = lists };
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            PeerInfo peer = new(syncPeer);
            peer.RecordNoProgress(AllocationContexts.Snap | AllocationContexts.Headers);
            peer.IncreaseWeakness(AllocationContexts.Snap | AllocationContexts.Headers);
            INodeStatsManager stats = Substitute.For<INodeStatsManager>();
            Synchronization.SnapSync.SnapSyncFeed feed = new(stats, provider, LimboLogs.Instance);
            long pivotAttempts = Metrics.ForcedStatePivotUpdates;
            Assert.That(await DispatchPreparedResponse(feed, storage, peer, blockTree, stats, cancellationToken),
                Is.EqualTo(responseShape == 2 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.Emptish));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.True);
                Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.True);
                Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.True);
                Assert.That(peer.HasWeakness(AllocationContexts.Headers), Is.True);
                Assert.That(Metrics.ForcedStatePivotUpdates, Is.EqualTo(pivotAttempts));
                Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(responseShape == 0 ? 0 : Math.Min(64, accountCount)));
            }
            if (responseShape == 2) stats.Received(1).ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, 0);
            else stats.DidNotReceive().ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
        }

        [Test, CancelAfter(30_000)]
        public async Task Storage_after_pivot_move_retains_committed_work_and_retries_only_unverified_accounts(
            [Values(0, 1, 1200)] int changedAccounts, [Values] Fault fault, CancellationToken cancellationToken)
        {
            using MemDb serverDb = new();
            TestRawTrieStore serverStore = new(serverDb);
            StateTree state = new(serverStore, LimboLogs.Instance);
            PathWithAccount[] accounts = new PathWithAccount[1200];
            using (serverStore.BeginBlockCommit(1))
            {
                for (int i = 0; i < accounts.Length; i++)
                {
                    Hash256 path = Keccak.Compute(System.BitConverter.GetBytes(i));
                    StorageTree storage = new(serverStore.GetTrieStore(path), LimboLogs.Instance);
                    storage.Set(ValueKeccak.Zero, [1], false);
                    storage.Commit();
                    Account account = new(0, 1, storage.RootHash, Keccak.OfAnEmptyString);
                    state.Set(path, account);
                    accounts[i] = new(path, account.ToStruct());
                }
                state.Commit();
            }
            System.Array.Sort(accounts, static (first, second) => first.Path.CompareTo(second.Path));
            SnapStateServer server = new(serverStore.AsReadOnly(), LimboLogs.Instance);
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
                .WithSuggestedHeaderOfStateRoot(state.RootHash).Build();
            ProgressTracker tracker = container.Resolve<ProgressTracker>();
            SnapProvider provider = container.Resolve<SnapProvider>();
            tracker.IsFinished(out SnapSyncBatch? accountBatch);
            Assert.That(provider.AddAccountRange(1, state.RootHash, ValueKeccak.Zero, accounts), Is.EqualTo(AddRangeResult.OK));
            provider.ReleaseRequest(accountBatch!, true);
            accountBatch!.Dispose();

            HashSet<ValueHash256> changed = [];
            if (changedAccounts > 0)
            {
                using (serverStore.BeginBlockCommit(129))
                {
                    for (int i = 0; i < changedAccounts; i++)
                    {
                        PathWithAccount account = accounts[changedAccounts == 1 ? 1198 : i];
                        StorageTree storage = new(serverStore.GetTrieStore(account.Path.ToCommitment()), LimboLogs.Instance);
                        storage.Set(ValueKeccak.Zero, [2], false);
                        storage.Commit();
                        state.Set(account.Path, new Account(0, 1, storage.RootHash, Keccak.OfAnEmptyString));
                        changed.Add(account.Path);
                    }
                    state.Commit();
                }
            }
            IBlockTree blockTree = container.Resolve<IBlockTree>();
            BlockHeader newPivot = Build.A.BlockHeader.WithNumber(129).WithStateRoot(state.RootHash).TestObject;
            blockTree.BestSuggestedHeader.Returns(newPivot);
            blockTree.FindHeader(Arg.Any<ulong>()).Returns(newPivot);
            provider.UpdatePivot();
            tracker.IsFinished(out SnapSyncBatch? storageBatch);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storageBatch!.StorageRangeRequest!.Accounts, Has.Count.EqualTo(1200));
                Assert.That(storageBatch.StorageRangeRequest.RootHash, Is.EqualTo(state.RootHash));
            }
            SetStorageResponse(server, storageBatch);
            HashSet<ValueHash256> unverified = [.. changed];
            if (fault == Fault.InvalidOrder)
            {
                SlotsAndProofs response = storageBatch.StorageRangeResponse!;
                using IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> encoded = response.PathsAndSlots;
                ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> slots = [with(encoded.Count)];
                slots.Add(new ArrayPoolList<PathWithStorageSlot>(2) { new(ValueKeccak.MaxValue, [1]), new(ValueKeccak.Zero, [1]) });
                for (int i = 1; i < encoded.Count; i++) slots.Add(new ArrayPoolList<PathWithStorageSlot>(encoded[i].Count, encoded[i]));
                response.PathsAndSlots = slots;
                unverified.Add(accounts[0].Path);
            }
            else if (fault == Fault.InvalidProof)
            {
                storageBatch.StorageRangeResponse!.Proofs.Dispose();
                storageBatch.StorageRangeResponse.Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { new byte[] { 0xc1, 0xc0 } });
                unverified.Add(accounts[^1].Path);
            }
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            PeerInfo peer = new(syncPeer);
            INodeStatsManager stats = container.Resolve<INodeStatsManager>();
            Synchronization.SnapSync.SnapSyncFeed feed = new(stats, provider, LimboLogs.Instance);
            ISyncPeer otherPeer = Substitute.For<ISyncPeer>();
            otherPeer.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.1", 30303));
            for (int i = 0; i < 5; i++) feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, fault == Fault.None ? new PeerInfo(otherPeer) : peer);
            peer.RecordNoProgress(AllocationContexts.Headers);
            peer.IncreaseWeakness(AllocationContexts.Headers);
            if (changedAccounts > 0 && fault == Fault.None)
            {
                peer.RecordNoProgress(AllocationContexts.Snap);
                peer.IncreaseWeakness(AllocationContexts.Snap);
            }

            SyncResponseHandlingResult result = await DispatchPreparedResponse(feed, storageBatch, peer, blockTree, stats, cancellationToken);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(fault != Fault.None ? SyncResponseHandlingResult.LesserQuality : changedAccounts > 0 ? SyncResponseHandlingResult.Emptish : SyncResponseHandlingResult.OK));
                Assert.That(stats.GetOrAdd(syncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.SnapRanges), Is.EqualTo(changedAccounts == 1200 && fault == Fault.None ? (long?)null : 33L * (1200 - unverified.Count)));
                Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(System.Math.Min(64, unverified.Count)));
                Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.EqualTo(fault == Fault.None && changedAccounts > 0));
                Assert.That(peer.HasWeakness(AllocationContexts.Snap), Is.EqualTo(fault != Fault.None || changedAccounts > 0));
                Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.True);
                Assert.That(peer.IsAsleep(AllocationContexts.Snap), Is.False);
                Assert.That(Metrics.SnapConsecutiveUnproductiveResponses, Is.EqualTo(unverified.Count < 1200 ? 0 : fault == Fault.None ? 5 : 6),
                    "committed storage clears the stall streak independently of peer fault history");
            }
            if (changedAccounts > 0 && fault == Fault.None)
                Assert.That(peer.IncreaseWeakness(AllocationContexts.Snap), Is.EqualTo(AllocationContexts.Snap), "neutral response preserves the existing weakness");
            HashSet<ValueHash256> refreshed = [];
            HashSet<ValueHash256> retried = [];
            int attempts = 0;
            while (!tracker.IsFinished(out SnapSyncBatch? retry))
            {
                Assert.That(++attempts, Is.LessThanOrEqualTo(3600));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(tracker.AccountsToRefreshCount, Is.LessThanOrEqualTo(64));
                    Assert.That(retry, Is.Not.Null);
                }
                if (retry!.AccountsToRefreshRequest is { } refresh)
                {
                    ValueHash256 path = refresh.Paths.Single().PathAndAccount!.Path;
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(refreshed.Add(path), Is.True, "each failed account needs only one refresh");
                        Assert.That(unverified, Does.Contain(path));
                    }
                    (IOwnedReadOnlyList<PathWithAccount> refreshedAccounts, IByteArrayList proofs) = server.GetAccountRanges(
                        state.RootHash, path, path.IncrementPath(), 4000, CancellationToken.None);
                    retry.AccountsToRefreshResponse = new() { PathAndAccounts = refreshedAccounts, Proofs = proofs };
                }
                else
                {
                    foreach (PathWithAccount account in retry.StorageRangeRequest!.Accounts)
                    {
                        if (refreshed.Contains(account.Path) || changedAccounts == 0)
                            Assert.That(retried.Add(account.Path), Is.True, "committed accounts must not be retried");
                        Assert.That(unverified, Does.Contain(account.Path));
                    }
                    SetStorageResponse(server, retry);
                }
                SyncResponseHandlingResult recovery = await DispatchPreparedResponse(feed, retry, peer, blockTree, stats, cancellationToken);
                Assert.That(recovery, Is.AnyOf(SyncResponseHandlingResult.OK, SyncResponseHandlingResult.Emptish));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(refreshed, Is.EquivalentTo(changedAccounts == 0 ? Array.Empty<ValueHash256>() : unverified));
                Assert.That(retried, Is.EquivalentTo(unverified));
                Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
                Assert.That(peer.HasNoProgress(AllocationContexts.Snap), Is.False);
                Assert.That(peer.HasNoProgress(AllocationContexts.Headers), Is.True);
            }
            for (int i = 0; i < accounts.Length; i++)
            {
                PathWithAccount account = accounts[i];
                StorageTree persisted = new(new RawScopedTrieStore(container.Resolve<INodeStorage>(), account.Path.ToCommitment()),
                    state.Get(account.Path.ToCommitment())!.StorageRoot, LimboLogs.Instance);
                Assert.That(persisted.Get(ValueKeccak.Zero.Bytes).ToArray(), Is.EqualTo(new byte[] { changed.Contains(account.Path) ? (byte)2 : (byte)1 }));
            }
        }

        [Test]
        public void Alternate_root_empty_continuation_proof_refreshes_only_proven_stale_account([Values] bool malformed, [Values] bool inlineBranch)
        {
            using MemDb serverDb = new();
            TestRawTrieStore store = new(serverDb);
            PathWithAccount account;
            StateTree state = new(store, LimboLogs.Instance);
            Hash256 oldStateRoot;
            using (store.BeginBlockCommit(1))
            {
                StorageTree oldStorage = new(store.GetTrieStore(TestItem.KeccakA), LimboLogs.Instance);
                oldStorage.Set(ValueKeccak.Zero, [1], false);
                oldStorage.Commit();
                account = new(TestItem.KeccakA, new Account(0, 1, oldStorage.RootHash, Keccak.OfAnEmptyString).ToStruct());
                state.Set(account.Path, account.Account.ToAccount());
                state.Commit();
                oldStateRoot = state.RootHash;
            }
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
                .WithSuggestedHeaderOfStateRoot(oldStateRoot).Build();
            ProgressTracker tracker = container.Resolve<ProgressTracker>();
            SnapProvider provider = container.Resolve<SnapProvider>();
            tracker.IsFinished(out SnapSyncBatch? discovered);
            Assert.That(provider.AddAccountRange(1, oldStateRoot, ValueKeccak.Zero, new[] { account }), Is.EqualTo(AddRangeResult.OK));
            provider.ReleaseRequest(discovered!, true);
            discovered!.Dispose();
            tracker.IsFinished(out SnapSyncBatch? fullStorage);
            provider.ReleaseRequest(fullStorage!, true);
            fullStorage!.Dispose();
            using (store.BeginBlockCommit(129))
            {
                StorageTree currentStorage = new(store.GetTrieStore(TestItem.KeccakA), LimboLogs.Instance);
                currentStorage.Set(ValueKeccak.Zero, [2], false);
                if (inlineBranch) currentStorage.Set(ValueKeccak.Zero.IncrementPath(), [3], false);
                currentStorage.Commit();
                state.Set(account.Path, account.Account!.ToAccount().WithChangedStorageRoot(currentStorage.RootHash));
                state.Commit();
            }
            ValueHash256 start = inlineBranch ? ValueKeccak.Zero.IncrementPath().IncrementPath() : TestItem.KeccakB;
            AccountProofCollector collector = new(account.Path.Bytes, [start]);
            state.Accept(collector, state.RootHash);
            byte[][] proof = collector.BuildResult().StorageProofs[0].Proof;
            IBlockTree blockTree = container.Resolve<IBlockTree>();
            BlockHeader pivot = Build.A.BlockHeader.WithNumber(129).WithStateRoot(state.RootHash).TestObject;
            blockTree.BestSuggestedHeader.Returns(pivot);
            blockTree.FindHeader(Arg.Any<ulong>()).Returns(pivot);
            provider.UpdatePivot();
            tracker.EnqueueNextSlot(new StorageRange
            {
                Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                StartingHash = start
            });
            tracker.IsFinished(out SnapSyncBatch? continuation);
            continuation!.StorageRangeResponse = new()
            {
                Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(malformed ? new byte[][] { [0xc1, 0xc0] } : proof))
            };
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            PeerInfo peer = new(syncPeer);
            peer.RecordNoProgress(AllocationContexts.Snap | AllocationContexts.Headers);
            peer.IncreaseWeakness(AllocationContexts.Snap | AllocationContexts.Headers);
            INodeStatsManager stats = Substitute.For<INodeStatsManager>();
            Synchronization.SnapSync.SnapSyncFeed feed = new(stats, provider, LimboLogs.Instance);
            Assert.That(feed.HandleResponse(continuation, peer),
                Is.EqualTo(malformed ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.Emptish));
            if (malformed)
            {
                stats.Received(1).ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, 0);
                Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
                return;
            }
            stats.DidNotReceive().ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
            Assert.That(tracker.AccountsToRefreshCount, Is.EqualTo(1));
            tracker.IsFinished(out SnapSyncBatch? refresh);
            AccountsToRefreshRequest refreshRequest = refresh!.AccountsToRefreshRequest!;
            Assert.That(refreshRequest.Paths[0].StorageStartingHash, Is.EqualTo(start));
            SnapStateServer server = new(store.AsReadOnly(), LimboLogs.Instance);
            (IOwnedReadOnlyList<PathWithAccount> values, IByteArrayList accountProof) =
                server.GetAccountRanges(state.RootHash, account.Path, account.Path.IncrementPath(), 4000, CancellationToken.None);
            refresh.AccountsToRefreshResponse = new() { PathAndAccounts = values, Proofs = accountProof };
            Assert.That(feed.HandleResponse(refresh, peer), Is.EqualTo(SyncResponseHandlingResult.OK));
            tracker.IsFinished(out SnapSyncBatch? resumed);
            Assert.That(resumed!.StorageRangeRequest!.StartingHash, Is.EqualTo(start));
            resumed.StorageRangeResponse = new() { Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proof)) };
            stats.ClearReceivedCalls();
            Assert.That(feed.HandleResponse(resumed, peer), Is.EqualTo(SyncResponseHandlingResult.OK));
            stats.DidNotReceive().ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
            }
        }

        [TestCase(500, true)]
        [TestCase(120000, false)]
        public void All_stale_unavailable_recovery_is_bounded(int accountCount, bool continuations)
        {
            using MemDb sourceDb = new();
            StateTree source = new(new RawScopedTrieStore(sourceDb), LimboLogs.Instance);
            using StorageRange sample = ProgressTrackerTests.CreateRequest(1);
            PathWithAccount[] accounts = new PathWithAccount[accountCount];
            ValueHash256 path = ValueKeccak.Zero;
            for (int i = 0; i < accounts.Length; i++)
            {
                accounts[i] = new(path, sample.Accounts[0].Account);
                source.Set(path, accounts[i].Account.ToAccount());
                path = path.IncrementPath();
            }
            source.UpdateRootHash();
            using IContainer container = new ContainerBuilder()
                .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
                .WithSuggestedHeaderOfStateRoot(source.RootHash).Build();
            ProgressTracker tracker = container.Resolve<ProgressTracker>();
            SnapProvider provider = container.Resolve<SnapProvider>();
            tracker.IsFinished(out SnapSyncBatch? discovered);
            Assert.That(provider.AddAccountRange(1, source.RootHash, ValueKeccak.Zero, accounts), Is.EqualTo(AddRangeResult.OK));
            provider.ReleaseRequest(discovered!, true);
            discovered!.Dispose();
            if (continuations)
            {
                while (!tracker.IsFinished(out SnapSyncBatch? initial) && initial is not null)
                {
                    provider.ReleaseRequest(initial, true);
                    initial.Dispose();
                }
                foreach (PathWithAccount account in accounts)
                    tracker.EnqueueNextSlot(new StorageRange
                    {
                        Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                        StartingHash = ValueKeccak.Zero.IncrementPath()
                    });
            }
            IBlockTree blockTree = container.Resolve<IBlockTree>();
            BlockHeader current = Build.A.BlockHeader.WithNumber(129).WithStateRoot(TestItem.KeccakF).TestObject;
            blockTree.BestSuggestedHeader.Returns(current);
            blockTree.FindHeader(Arg.Any<ulong>()).Returns(current);
            provider.UpdatePivot();
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            INodeStatsManager stats = Substitute.For<INodeStatsManager>();
            Synchronization.SnapSync.SnapSyncFeed feed = new(stats, provider, LimboLogs.Instance);
            PeerInfo peer = new(syncPeer);
            long suppressed = Metrics.ForcedStatePivotUpdatesSuppressed;
            const int emptyResponseThreshold = 12;
            const int refreshReplies = 6;
            int continuationTurns = continuations ? (refreshReplies - 1) / ProgressTracker.MAX_CONSECUTIVE_ACCOUNT_REFRESHES : 0;
            int expectedStorageReplies = (emptyResponseThreshold - 1) *
                (continuations ? accountCount : (accountCount + 1199) / 1200) + 1 + continuationTurns;
            int storageReplies = 0;
            int unavailableReplies = 0;
            for (int attempt = 0; unavailableReplies < refreshReplies; attempt++)
            {
                Assert.That(attempt, Is.LessThan(expectedStorageReplies + refreshReplies));
                tracker.IsFinished(out SnapSyncBatch? batch);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(batch, Is.Not.Null);
                    Assert.That(tracker.AccountsToRefreshCount, Is.LessThanOrEqualTo(64));
                }
                if (batch!.StorageRangeRequest is not null)
                {
                    storageReplies++;
                    batch.StorageRangeResponse = new();
                    Assert.That(feed.HandleResponse(batch, peer), Is.EqualTo(SyncResponseHandlingResult.Emptish));
                }
                else
                {
                    Assert.That(batch.AccountsToRefreshRequest, Is.Not.Null);
                    unavailableReplies++;
                    TestContext.Out.WriteLine($"Unavailable refresh {unavailableReplies} after {storageReplies} storage replies, {tracker.Recovery.CohortSlots} retained cohort slots.");
                    batch.AccountsToRefreshResponse = new();
                    Assert.That(feed.HandleResponse(batch, peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
                }
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storageReplies, Is.EqualTo(expectedStorageReplies));
                Assert.That(unavailableReplies, Is.EqualTo(6));
                Assert.That(tracker.Recovery.CohortSlots, Is.EqualTo(continuations ? 1 + continuationTurns : SnapProvider.MaxQueuedEmptyStreakRefreshes));

                Assert.That(Metrics.ForcedStatePivotUpdatesSuppressed - suppressed, Is.EqualTo(1));
            }
            stats.Received(6).ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, 0);
            BlockHeader recovered = Build.A.BlockHeader.WithNumber(257).WithStateRoot(source.RootHash).TestObject;
            blockTree.BestSuggestedHeader.Returns(recovered);
            blockTree.FindHeader(Arg.Any<ulong>()).Returns(recovered);
            provider.UpdatePivot();
            using MemDb storageDb = new();
            RawScopedTrieStore storageStore = new(storageDb);
            StorageTree storage = new(storageStore, LimboLogs.Instance);
            storage.Set(ValueKeccak.Zero, [1], false);
            storage.Commit();
            byte[] proof = storageStore.LoadRlp(TreePath.Empty, storage.RootHash)!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(proof, Is.Not.Empty);
                Assert.That(ValueKeccak.Compute(proof), Is.EqualTo(storage.RootHash.ValueHash256));
            }
            HashSet<ValueHash256> retained = [];
            for (int guard = 0; !tracker.IsSnapGetRangesFinished(); guard++)
            {
                Assert.That(guard, Is.LessThanOrEqualTo(accountCount + 64));
                tracker.IsFinished(out SnapSyncBatch? batch);
                Assert.That(batch!.StorageRangeRequest, Is.Not.Null, "authenticated original evidence completes the admitted refresh cohort locally");
                foreach (PathWithAccount account in batch.StorageRangeRequest!.Accounts)
                    Assert.That(retained.Add(account.Path), Is.True, "ordinary work and admitted refresh ownership each recover once");
                using SlotsAndProofs response = batch.StorageRangeRequest.StartingHash.GetValueOrDefault() != ValueKeccak.Zero
                    ? new() { Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { proof }) }
                    : new() { PathsAndSlots = batch.StorageRangeRequest.Accounts.Select(_ => ProgressTrackerTests.ValidSlots()).ToArray().ToPooledList() };
                AddRangeResult directEmpty = response.PathsAndSlots.Count == 0
                    ? SnapProviderHelper.VerifyEmptyStorageRange(batch.StorageRangeRequest, response.Proofs)
                    : AddRangeResult.OK;
                Assert.That(provider.AddStorageRange(batch.StorageRangeRequest, response), Is.EqualTo(AddRangeResult.OK),
                    $"start {batch.StorageRangeRequest.StartingHash}, limit {batch.StorageRangeRequest.LimitHash}, lists {response.PathsAndSlots.Count}, account root {batch.StorageRangeRequest.Accounts[0].Account!.StorageRoot}, proof root {storage.RootHash}, direct empty {directEmpty}");
                provider.ReleaseRequest(batch, true);
                batch.Dispose();
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retained.Count, Is.EqualTo(accountCount));
                Assert.That(tracker.Recovery.CohortSlots, Is.Zero);
                Assert.That(tracker.AccountsToRefreshCount, Is.Zero);
            }
        }

        [Test]
        public void GetH_shaped_omission_keeps_current_success_and_stale_streaks([Values] bool bogusRoot)
        {
            using OmissionContext context = new(1200, duplicateRoot: false);
            context.PrepareResponse([bogusRoot ? Rlp.Encode(5000) : context.Values[^1]]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Feed.HandleResponse(context.Batch!, context.Peer), Is.EqualTo(SyncResponseHandlingResult.Emptish));
                Assert.That(context.Tracker.AccountsToRefreshCount, Is.EqualTo(bogusRoot ? 1 : 0));
            }
            if (bogusRoot)
                context.Stats.DidNotReceive().ReportTransferSpeedEvent(context.Peer.SyncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
            else
            {
                context.Stats.Received(1).ReportTransferSpeedEvent(context.Peer.SyncPeer.Node, TransferSpeedType.SnapRanges, 32 + context.Values[^1].Length);
                for (int i = 0; i < 1199; i++) Assert.That(context.Original[i].EmptyStorageResponses, Is.EqualTo(1));
                Assert.That(context.Original[^1].EmptyStorageResponses, Is.Zero);
            }
        }

        [Test]
        public void Duplicate_root_lineage_does_not_fault_honest_shifted_current_sibling()
        {
            using OmissionContext context = new(3, duplicateRoot: true);
            context.PrepareResponse([context.Values[1], context.Values[2]]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Feed.HandleResponse(context.Batch!, context.Peer), Is.EqualTo(SyncResponseHandlingResult.Emptish));
                Assert.That(context.Tracker.AccountsToRefreshCount, Is.Zero);
            }
            foreach (PathWithAccount account in context.Current) Assert.That(account.EmptyStorageResponses, Is.Zero);
            context.Tracker.IsFinished(out SnapSyncBatch? retry);
            Assert.That(retry!.StorageRangeRequest!.Accounts.Select(account => account.Path),
                Is.EquivalentTo(context.Current.Select(account => account.Path)));
            context.Provider.ReleaseRequest(retry, true);
            retry.Dispose();
            Assert.That(context.Tracker.IsSnapGetRangesFinished(), Is.True);
        }

        [Test]
        public void Duplicate_root_lineage_verifies_canonical_trigger_and_tail_proofs([Values] bool tail, [Values] bool contradictory)
        {
            using OmissionContext context = new(tail ? 4 : 3, duplicateRoot: true);
            context.PrepareResponse(tail ? [context.Values[1], context.Values[2], context.Values[3]] : [context.Values[1], context.Values[2]]);
            byte[] proof = ProgressTrackerTests.CreateSingleSlotProof(
                contradictory ? Rlp.Encode(9999) : context.Values[tail ? 3 : 2]);
            context.Batch!.StorageRangeResponse!.Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { proof });
            Assert.That(context.Feed.HandleResponse(context.Batch, context.Peer),
                Is.EqualTo(contradictory ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.Emptish));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Tracker.AccountsToRefreshCount, Is.Zero);
                foreach (PathWithAccount account in context.Current) Assert.That(account.EmptyStorageResponses, Is.Zero);
            }
        }

        [Test]
        public void Twelfth_omission_preserves_history_and_served_storage_resets_it([Values] bool proofBearing, [Values] bool omitted)
        {
            using OmissionContext context = new(2, duplicateRoot: false, proofBearing: proofBearing);
            context.MovePivot(TestItem.KeccakF);
            for (int i = 0; i < 11; i++)
            {
                context.PrepareEmptyResponse();
                Assert.That(context.Feed.HandleResponse(context.Batch!, context.Peer), Is.EqualTo(SyncResponseHandlingResult.Emptish));
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Original[0].EmptyStorageResponses, Is.EqualTo(11));
                Assert.That(context.Current[0].EmptyStorageResponses, Is.EqualTo(11));
            }
            context.MovePivot();
            if (proofBearing) context.PrepareProofResponse(includeFirst: !omitted);
            else context.PrepareResponse(omitted ? [context.Values[1]] : [context.Values[0], context.Values[1]]);
            Assert.That(context.Feed.HandleResponse(context.Batch!, context.Peer),
                Is.EqualTo(omitted ? SyncResponseHandlingResult.Emptish : SyncResponseHandlingResult.OK));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Original[0].EmptyStorageResponses, Is.Zero);
                Assert.That(context.Current[0].EmptyStorageResponses, Is.Zero);
                Assert.That(context.Tracker.AccountsToRefreshCount, Is.EqualTo(omitted ? 1 : 0));
            }
        }

        [Test]
        public void Verified_empty_or_deleted_account_rejects_overlapping_nonempty_storage([Values] bool deleted)
        {
            using MemDb db = new();
            TestRawTrieStore store = new(db);
            StateTree state = new(store, LimboLogs.Instance);
            PathWithAccount account;
            PathWithAccount neighbor = new(ValueKeccak.MaxValue, new Account(0, 1).ToStruct());
            using (store.BeginBlockCommit(1))
            {
                StorageTree storage = new(store.GetTrieStore(Keccak.Zero), LimboLogs.Instance);
                storage.Set(ValueKeccak.Zero, [1], false);
                storage.Commit();
                account = new(ValueKeccak.Zero, new Account(0, 1, storage.RootHash, Keccak.OfAnEmptyString).ToStruct());
                state.Set(account.Path, account.Account.ToAccount());
                state.Set(neighbor.Path, neighbor.Account.ToAccount());
                state.Commit();
            }
            using IContainer container = ProgressTrackerTests.CreateContainer(root: state.RootHash);
            ProgressTracker tracker = container.Resolve<ProgressTracker>();
            SnapProvider provider = container.Resolve<SnapProvider>();
            tracker.IsFinished(out SnapSyncBatch? discovered);
            Assert.That(provider.AddAccountRange(1, state.RootHash, ValueKeccak.Zero, new[] { account, neighbor }), Is.EqualTo(AddRangeResult.OK));
            provider.ReleaseRequest(discovered!, true);
            discovered!.Dispose();
            tracker.IsFinished(out SnapSyncBatch? initial);
            provider.ReleaseRequest(initial!, true);
            initial!.Dispose();
            using (store.BeginBlockCommit(129))
            {
                state.Set(account.Path, deleted ? null : new Account(0, 2));
                state.Commit();
            }
            IBlockTree blockTree = container.Resolve<IBlockTree>();
            BlockHeader pivot = Build.A.BlockHeader.WithNumber(129).WithStateRoot(state.RootHash).TestObject;
            blockTree.BestSuggestedHeader.Returns(pivot);
            blockTree.FindHeader(Arg.Any<ulong>()).Returns(pivot);
            provider.UpdatePivot();
            tracker.EnqueueAccountStorage(account);
            tracker.IsFinished(out SnapSyncBatch? overlapping);
            Assert.That(overlapping!.StorageRangeRequest!.RootHash, Is.EqualTo(state.RootHash));
            tracker.EnqueueAccountRefresh(account, null, null);
            tracker.IsFinished(out SnapSyncBatch? refresh);
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);
            SnapStateServer server = new(store.AsReadOnly(), LimboLogs.Instance);
            (IOwnedReadOnlyList<PathWithAccount> values, IByteArrayList proofs) = server.GetAccountRanges(
                state.RootHash, account.Path, account.Path.IncrementPath(), 4000, CancellationToken.None);
            refresh.AccountsToRefreshResponse = new() { PathAndAccounts = values, Proofs = proofs };
            ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
            syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
            PeerInfo peer = new(syncPeer);
            INodeStatsManager stats = Substitute.For<INodeStatsManager>();
            Synchronization.SnapSync.SnapSyncFeed feed = new(stats, provider, LimboLogs.Instance);
            Assert.That(feed.HandleResponse(refresh, peer), Is.EqualTo(SyncResponseHandlingResult.OK));
            stats.ClearReceivedCalls();
            overlapping.StorageRangeResponse = new()
            {
                PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1) { ProgressTrackerTests.ValidSlots() }
            };
            Assert.That(feed.HandleResponse(overlapping, peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
            stats.Received(1).ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(account.Account!.HasStorage, Is.True);
                Assert.That(tracker.IsFinished(out SnapSyncBatch? next), Is.True);
                Assert.That(next, Is.Null);
            }
        }

        [Test]
        public void Proof_bearing_omission_verifies_the_permitted_current_account([Values] bool forgedSlot)
        {
            using OmissionContext context = new(2, duplicateRoot: false, proofBearing: true);
            context.PrepareProofResponse();
            if (forgedSlot)
            {
                SlotsAndProofs response = context.Batch!.StorageRangeResponse!;
                response.PathsAndSlots.Dispose();
                response.PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
                {
                    new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, Rlp.Encode(5000)) }
                };
            }
            Assert.That(context.Feed.HandleResponse(context.Batch!, context.Peer),
                Is.EqualTo(forgedSlot ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.Emptish));
            if (forgedSlot)
                context.Stats.Received(1).ReportTransferSpeedEvent(context.Peer.SyncPeer.Node, TransferSpeedType.SnapRanges, 0);
            else
            {
                context.Stats.Received(1).ReportTransferSpeedEvent(context.Peer.SyncPeer.Node, TransferSpeedType.SnapRanges, 32 + context.Values[^1].Length);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(context.Original[0].EmptyStorageResponses, Is.EqualTo(1));
                    Assert.That(context.Tracker.AccountsToRefreshCount, Is.Zero);
                }
            }
        }

        [Test]
        public void Realigned_current_prefix_does_not_hide_surplus_tail([Values] bool malformedTail, [Values] bool sparse)
        {
            using OmissionContext context = new(sparse ? 3 : 2, duplicateRoot: false, omitOnlyFirst: sparse);
            context.PrepareResponse([context.Values[1], malformedTail ? new byte[] { 0xff } : context.Values[1]]);
            Assert.That(context.Feed.HandleResponse(context.Batch!, context.Peer),
                Is.EqualTo(sparse || malformedTail ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.Emptish));
            context.Stats.Received(1).ReportTransferSpeedEvent(context.Peer.SyncPeer.Node, TransferSpeedType.SnapRanges,
                sparse || !malformedTail ? 32 + context.Values[1].Length : 0);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(context.Original[0].EmptyStorageResponses, Is.EqualTo(sparse ? 1 : 0));
            }
        }

        [Test]
        public void Duplicate_root_ambiguity_does_not_hide_malformed_tail()
        {
            using OmissionContext context = new(4, duplicateRoot: true);
            context.PrepareResponse([context.Values[1], context.Values[2], context.Values[3]]);
            ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> lists =
                (ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>)context.Batch!.StorageRangeResponse!.PathsAndSlots;
            lists[2].Dispose();
            lists[2] = new ArrayPoolList<PathWithStorageSlot>(2)
            {
                new(ValueKeccak.Zero, context.Values[3]), new(ValueKeccak.Zero, context.Values[3])
            };
            Assert.That(context.Feed.HandleResponse(context.Batch, context.Peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
        }

        private sealed class OmissionContext : System.IDisposable
        {
            private readonly MemDb _db = new();
            private readonly IContainer _container;
            private readonly SnapStateServer _server;
            private readonly Hash256 _root;
            public PathWithAccount[] Original { get; }
            public PathWithAccount[] Current { get; }
            public byte[][] Values { get; }
            public ProgressTracker Tracker { get; }
            public SnapProvider Provider { get; }
            public INodeStatsManager Stats { get; } = Substitute.For<INodeStatsManager>();
            public PeerInfo Peer { get; }
            public Synchronization.SnapSync.SnapSyncFeed Feed { get; }
            public SnapSyncBatch? Batch { get; private set; }

            public OmissionContext(int count, bool duplicateRoot, bool proofBearing = false, bool omitOnlyFirst = false)
            {
                TestRawTrieStore store = new(_db);
                StateTree state = new(store, LimboLogs.Instance);
                Original = new PathWithAccount[count];
                Values = new byte[count][];
                ValueHash256 path = ValueKeccak.Zero;
                using (store.BeginBlockCommit(1))
                {
                    for (int i = 0; i < count; i++)
                    {
                        Values[i] = Rlp.Encode(duplicateRoot && i == 0 ? 2 : i + 1);
                        StorageTree storage = new(store.GetTrieStore(path.ToCommitment()), LimboLogs.Instance);
                        storage.Set(ValueKeccak.Zero, Values[i], false);
                        if (proofBearing && i == count - 1) storage.Set(ValueKeccak.MaxValue, Rlp.Encode(i + 1000), false);
                        storage.Commit();
                        Original[i] = new(path, new Account(0, 1, storage.RootHash, Keccak.OfAnEmptyString).ToStruct());
                        state.Set(path, Original[i].Account.ToAccount());
                        path = path.IncrementPath();
                    }
                    state.Commit();
                }
                _container = new ContainerBuilder()
                    .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
                    .WithSuggestedHeaderOfStateRoot(state.RootHash).Build();
                Tracker = _container.Resolve<ProgressTracker>();
                Provider = _container.Resolve<SnapProvider>();
                Tracker.IsFinished(out SnapSyncBatch? discovered);
                Assert.That(Provider.AddAccountRange(1, state.RootHash, ValueKeccak.Zero, Original), Is.EqualTo(AddRangeResult.OK));
                Provider.ReleaseRequest(discovered!, true);
                discovered!.Dispose();
                Tracker.IsFinished(out SnapSyncBatch? initial);
                Provider.ReleaseRequest(initial!, true);
                initial!.Dispose();
                int omitted = duplicateRoot || omitOnlyFirst ? 1 : count - 1;
                using (store.BeginBlockCommit(129))
                {
                    for (int i = 0; i < omitted; i++) state.Set(Original[i].Path, new Account(0, 2));
                    state.Commit();
                }
                IBlockTree blockTree = _container.Resolve<IBlockTree>();
                BlockHeader pivot = Build.A.BlockHeader.WithNumber(129).WithStateRoot(state.RootHash).TestObject;
                blockTree.BestSuggestedHeader.Returns(pivot);
                blockTree.FindHeader(Arg.Any<ulong>()).Returns(pivot);
                Provider.UpdatePivot();
                for (int i = 0; i < omitted; i++) Tracker.EnqueueAccountStorage(Original[i]);
                _server = new(store.AsReadOnly(), LimboLogs.Instance);
                _root = state.RootHash;
                (IOwnedReadOnlyList<PathWithAccount> values, IByteArrayList proofs) = _server.GetAccountRanges(
                    state.RootHash, Original[omitted].Path, ValueKeccak.MaxValue, 2_000_000, CancellationToken.None);
                using AccountsAndProofs accepted = new() { PathAndAccounts = values, Proofs = proofs };
                Assert.That(Provider.AddAccountRange(129, state.RootHash, Original[omitted].Path, accepted.PathAndAccounts, accepted.Proofs), Is.EqualTo(AddRangeResult.OK));
                Current = accepted.PathAndAccounts.ToArray();
                ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
                syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
                Peer = new(syncPeer);
                Peer.RecordNoProgress(AllocationContexts.Snap | AllocationContexts.Headers);
                Peer.IncreaseWeakness(AllocationContexts.Snap | AllocationContexts.Headers);
                Feed = new(Stats, Provider, LimboLogs.Instance);
            }

            public void PrepareResponse(byte[][] values)
            {
                Batch = PrepareMixedRequest();
                ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> lists = [with(values.Length)];
                foreach (byte[] value in values) lists.Add(new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, value) });
                Batch!.StorageRangeResponse = new() { PathsAndSlots = lists };
                Batch.ResponseMilliseconds = 1;
            }

            private SnapSyncBatch PrepareMixedRequest()
            {
                Tracker.IsFinished(out SnapSyncBatch? batch);
                Assert.That(batch!.StorageRangeRequest, Is.Not.Null);
                if (batch.StorageRangeRequest!.Accounts.Count < Original.Length)
                {
                    Tracker.IsFinished(out SnapSyncBatch? current);
                    Assert.That(current!.StorageRangeRequest, Is.Not.Null);
                    ArrayPoolList<PathWithAccount> accounts = [with(Original.Length), .. batch.StorageRangeRequest.Accounts, .. current.StorageRangeRequest!.Accounts];
                    batch.StorageRangeRequest.Accounts.Dispose();
                    batch.StorageRangeRequest.Accounts = accounts;
                    current.Dispose();
                }
                Assert.That(batch.StorageRangeRequest.Accounts.Count, Is.EqualTo(Original.Length));
                return batch;
            }

            public void MovePivot(Hash256? root = null)
            {
                IBlockTree tree = _container.Resolve<IBlockTree>();
                BlockHeader header = Build.A.BlockHeader.WithNumber(tree.BestSuggestedHeader!.Number + 128).WithStateRoot(root ?? _root).TestObject;
                tree.BestSuggestedHeader.Returns(header);
                tree.FindHeader(Arg.Any<ulong>()).Returns(header);
                Provider.UpdatePivot();
            }

            public void PrepareEmptyResponse()
            {
                Batch = PrepareMixedRequest();
                Assert.That(Batch!.StorageRangeRequest, Is.Not.Null);
                Batch.StorageRangeResponse = new();
            }

            public void PrepareEmptyProofResponse(byte[] proof, ValueHash256 start)
            {
                Tracker.IsFinished(out SnapSyncBatch? unused);
                Provider.ReleaseRequest(unused!, true);
                unused!.Dispose();
                Tracker.EnqueueNextSlot(new StorageRange
                {
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { Original[0] },
                    StartingHash = start
                });
                Tracker.IsFinished(out SnapSyncBatch? batch);
                Batch = batch;
                Assert.That(Batch!.StorageRangeRequest!.StartingHash, Is.EqualTo(start));
                Batch.StorageRangeResponse = new()
                {
                    Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { proof })
                };
            }

            public void PrepareProofResponse(bool includeFirst = false)
            {
                Batch = PrepareMixedRequest();
                (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) =
                    _server.GetStorageRanges(_root, [Current[^1]], ValueKeccak.Zero, ValueKeccak.MaxValue, 1, CancellationToken.None);
                if (includeFirst)
                {
                    ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> combined = new(2)
                    {
                        new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, Values[0]) },
                        new ArrayPoolList<PathWithStorageSlot>(slots[0].Count, slots[0])
                    };
                    slots.Dispose();
                    slots = combined;
                }
                Batch!.StorageRangeResponse = new() { PathsAndSlots = slots, Proofs = proofs };
                Batch.ResponseMilliseconds = 1;
                Assert.That(proofs!.Count, Is.GreaterThan(0));
            }

            public void Dispose()
            {
                _container.Dispose();
                _db.Dispose();
            }
        }

        private static void SetReturnedProofListShape(OmissionContext context, ValueHash256 start, int returnedListShape, bool coherent)
        {
            if (returnedListShape == 0) return;
            if (returnedListShape == 2 && coherent)
            {
                using MemDb db = new();
                TestRawTrieStore store = new(db);
                StateTree state = new(store, LimboLogs.Instance);
                using (store.BeginBlockCommit(1))
                {
                    StorageTree storage = new(store.GetTrieStore(context.Original[0].Path.ToCommitment()), LimboLogs.Instance);
                    storage.Set(start, context.Values[0], false);
                    storage.Set(ValueKeccak.MaxValue, Rlp.Encode(1000), false);
                    storage.Commit();
                    state.Set(context.Original[0].Path, new Account(0, 1, storage.RootHash, Keccak.OfAnEmptyString));
                    state.Commit();
                }
                SnapStateServer server = new(store.AsReadOnly(), LimboLogs.Instance);
                context.Batch!.StorageRangeResponse!.Dispose();
                StorageRange request = context.Batch.StorageRangeRequest!;
                (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) = server.GetStorageRanges(
                    state.RootHash, request.Accounts, start, request.LimitHash, 1, CancellationToken.None);
                context.Batch.StorageRangeResponse = new() { PathsAndSlots = slots, Proofs = proofs };
                Assert.That(slots, Has.Count.EqualTo(1));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(slots[0], Has.Count.EqualTo(1));
                    Assert.That(proofs, Is.Not.Null);
                }
                return;
            }
            context.Batch!.StorageRangeResponse!.PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
            {
                returnedListShape == 1
                    ? new ArrayPoolList<PathWithStorageSlot>(0)
                    : new ArrayPoolList<PathWithStorageSlot>(1) { new(start, context.Values[0]) }
            };
        }

        private static void SetStorageResponse(ISnapStateServer server, SnapSyncBatch batch)
        {
            StorageRange request = batch.StorageRangeRequest!;
            (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) = server.GetStorageRanges(
                request.RootHash!, request.Accounts, request.StartingHash, request.LimitHash, 2_000_000, CancellationToken.None);
            batch.StorageRangeResponse = new() { PathsAndSlots = slots, Proofs = proofs };
            batch.ResponseMilliseconds = 1;
        }

        private static async Task<SyncResponseHandlingResult> DispatchPreparedResponse(
            Synchronization.SnapSync.SnapSyncFeed feed, SnapSyncBatch batch, PeerInfo peer, IBlockTree blockTree,
            INodeStatsManager stats, CancellationToken cancellationToken)
        {
            await using SyncPeerPool qualityPool = new(blockTree, stats, new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), LimboLogs.Instance);
            ISyncPeerPool allocationPool = Substitute.For<ISyncPeerPool>();
            SyncPeerAllocation allocation = new(AllocationContexts.Snap);
            allocation.AllocatePeer(peer);
            allocationPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), AllocationContexts.Snap, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(allocation));
            allocationPool.When(pool => pool.ReportNoSyncProgress(peer, AllocationContexts.Snap))
                .Do(_ => qualityPool.ReportNoSyncProgress(peer, AllocationContexts.Snap));
            allocationPool.When(pool => pool.ReportWeakPeer(peer, AllocationContexts.Snap))
                .Do(_ => qualityPool.ReportWeakPeer(peer, AllocationContexts.Snap));
            PreparedResponseFeed prepared = new(feed, batch);
            SimpleDispatcher<SnapSyncBatch> dispatcher = new(prepared, Substitute.For<ISyncDownloader<SnapSyncBatch>>(),
                new StaticPeerAllocationStrategyFactory<SnapSyncBatch>(FirstFree.Instance), AllocationContexts.Snap,
                allocationPool, new TestSyncConfig { MaxProcessingThreads = 1 }, LimboLogs.Instance);
            await dispatcher.Run(cancellationToken);
            if (prepared.Result == SyncResponseHandlingResult.NoProgress)
                allocationPool.Received(1).ReportNoSyncProgress(peer, AllocationContexts.Snap);
            else allocationPool.DidNotReceive().ReportNoSyncProgress(peer, AllocationContexts.Snap);
            return prepared.Result;
        }

        private sealed class PreparedResponseFeed(Synchronization.SnapSync.SnapSyncFeed feed, SnapSyncBatch batch) : ISimpleSyncFeed<SnapSyncBatch>
        {
            private SnapSyncBatch? _batch = batch;
            public SyncResponseHandlingResult Result { get; private set; }
            public Task<SnapSyncBatch?> PrepareRequest(CancellationToken cancellationToken) =>
                Task.FromResult(Interlocked.Exchange(ref _batch, null));
            public SyncResponseHandlingResult HandleResponse(SnapSyncBatch response, PeerInfo? peer = null) =>
                Result = feed.HandleResponse(response, peer);
        }

        [Test]
        public async Task Failed_range_records_one_feed_sample_and_BAL_defers_sampling_to_the_fetcher(
            [Values] bool deferred, [Values(0, 1, 2)] int failureKind)
        {
            Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            ISession session = Substitute.For<ISession>();
            session.Node.Returns(node);
            INodeStatsManager stats = Substitute.For<INodeStatsManager>();
            stats.GetOrAdd(node).Returns(new NodeStatsLight(node));
            stats.GetStatsOrNull(node).Returns(_ => stats.GetOrAdd(node));
            using MeasuredHandler handler = new(session, stats);
            using IContainer container = ProgressTrackerTests.CreateContainer();
            ProgressTracker tracker = container.Resolve<ProgressTracker>();
            SnapProvider provider = container.Resolve<SnapProvider>();
            tracker.IsFinished(out SnapSyncBatch? batch);
            using CancellationTokenSource cancellation = new();
            Task pending;
            if (failureKind == 2) pending = handler.FailCompletedRequest(deferred);
            else
            {
                pending = deferred
                    ? handler.GetAccountRange(batch!.AccountRangeRequest!, cancellation.Token)
                    : handler.GetBlockAccessLists([TestItem.KeccakA.ValueHash256], cancellation.Token);
                if (failureKind == 1) cancellation.Cancel();
            }
            if (failureKind == 2) await Assert.ThrowsAsync<IOException>(async () => await pending);
            else await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            if (deferred)
            {
                ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
                syncPeer.Node.Returns(node);
                Synchronization.SnapSync.SnapSyncFeed feed = new(stats, provider, LimboLogs.Instance);
                feed.HandleResponse(batch!, new PeerInfo(syncPeer));
                SnapSyncBatch? retry;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(tracker.IsFinished(out retry), Is.False);
                    Assert.That(retry!.AccountRangeRequest, Is.Not.Null, "a timed-out range must remain schedulable");
                }
                provider.ReleaseRequest(retry, true);
                retry.Dispose();
            }
            else
            {
                provider.ReleaseRequest(batch!, true);
                batch!.Dispose();
            }
            if (deferred) stats.Received(1).ReportTransferSpeedEvent(node, TransferSpeedType.SnapRanges, 0);
            else stats.DidNotReceive().ReportTransferSpeedEvent(node, TransferSpeedType.BlockAccessLists, Arg.Any<long>());
        }

        private sealed class MeasuredHandler(ISession session, INodeStatsManager stats)
            : Snap2ProtocolHandler(session, stats, new MessageSerializationService(), RunImmediatelyScheduler.Instance,
                LimboLogs.Instance, new SyncConfig(), NoopSnapServer.Instance)
        {
            public Task<IByteArrayList> FailCompletedRequest(bool deferred)
            {
                Request<string, IByteArrayList> request = new("failed response");
                request.CompletionSource.SetException(new IOException("connection closed"));
                return HandleResponse(request, deferred ? TransferSpeedType.SnapRanges : TransferSpeedType.BlockAccessLists,
                    static message => message, CancellationToken.None, recordResponseSpeed: false);
            }
        }
    }
}
