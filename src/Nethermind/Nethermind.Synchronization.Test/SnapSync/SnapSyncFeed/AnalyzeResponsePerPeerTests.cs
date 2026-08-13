// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync.SnapSyncFeed
{
    [TestFixture]
    internal class AnalyzeResponsePerPeerTests
    {
        [Test]
        public void Test01()
        {
            PeerInfo peer1 = new(null!);
            PeerInfo peer2 = new(null!);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);

            SyncResponseHandlingResult result = feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);

            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
        }

        [Test]
        public void Test02()
        {
            PeerInfo peer1 = new(null!);
            PeerInfo peer2 = new(null!);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);

            SyncResponseHandlingResult result = feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);

            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK));
        }

        [Test]
        public void Test03()
        {
            PeerInfo peer1 = new(null!);
            PeerInfo peer2 = new(null!);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            SyncResponseHandlingResult result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK));

            snapProvider.Received(1).UpdatePivot();
        }

        [Test]
        public void Test04()
        {
            PeerInfo peer1 = new(null!);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            for (int i = 0; i < 200; i++)
            {
                feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            }
        }

        // Regression for #6803: with a single peer connected, repeated failures must trigger
        // a pivot refresh rather than punish the only peer available.
        [Test]
        public void Single_peer_with_consecutive_failures_refreshes_pivot_instead_of_punishing()
        {
            PeerInfo peer = new(null!);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            SyncResponseHandlingResult? lastResult = null;
            for (int i = 0; i <= 6; i++)
            {
                lastResult = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer);
            }

            Assert.That(lastResult, Is.EqualTo(SyncResponseHandlingResult.OK));
            snapProvider.Received(1).UpdatePivot();
        }

        // When a single peer has produced a recent success, a brief failure burst must still
        // be tolerated and not trigger a pivot refresh on the first failure threshold breach.
        [Test]
        public void Single_peer_with_recent_success_is_not_punished_below_threshold()
        {
            PeerInfo peer = new(null!);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer);
            for (int i = 0; i < AllowedInvalidResponses; i++)
            {
                SyncResponseHandlingResult result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer);
                Assert.That(result, Is.Not.EqualTo(SyncResponseHandlingResult.LesserQuality));
            }

            snapProvider.DidNotReceive().UpdatePivot();
        }

        // Regression: a freshly added peer that fails its first AllowedInvalidResponses
        // requests must still be punished when the log contains entries from other,
        // healthy peers — even if those entries sit further back than the newcomer's
        // recent failures.
        [Test]
        public void New_peer_failing_burst_is_punished_when_log_holds_other_healthy_peers()
        {
            PeerInfo healthyPeer = new(null!);
            PeerInfo newPeer = new(null!);

            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            for (int i = 0; i < AllowedInvalidResponses; i++)
            {
                feed.AnalyzeResponsePerPeer(AddRangeResult.OK, healthyPeer);
            }

            SyncResponseHandlingResult? lastResult = null;
            for (int i = 0; i <= AllowedInvalidResponses; i++)
            {
                lastResult = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, newPeer);
            }

            Assert.That(lastResult, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
            snapProvider.DidNotReceive().UpdatePivot();
        }

        [Test]
        public void Punishes_the_only_peer_once_it_keeps_failing_across_a_pivot_update()
        {
            PeerInfo peer = new(null!);
            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();
            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            SyncResponseHandlingResult result = SyncResponseHandlingResult.OK;
            for (int i = 0; i <= AllowedInvalidResponses; i++)
            {
                result = feed.AnalyzeResponsePerPeer(AddRangeResult.EmptyRange, peer);
            }

            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK),
                "the first failure streak from the only peer reads as a stale pivot, not a bad peer");
            snapProvider.Received(1).UpdatePivot();

            for (int i = 0; i <= AllowedInvalidResponses; i++)
            {
                result = feed.AnalyzeResponsePerPeer(AddRangeResult.EmptyRange, peer);
            }

            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality),
                "a second streak from the same peer with no success since the pivot update means the peer itself is the problem");

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer);
            for (int i = 0; i <= AllowedInvalidResponses; i++)
            {
                result = feed.AnalyzeResponsePerPeer(AddRangeResult.EmptyRange, peer);
            }

            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK),
                "a success in between resets the guard back to the stale-pivot reading");
        }

        [Test]
        public void Does_not_punish_a_different_peer_for_the_previous_peers_pivot_update()
        {
            PeerInfo peerA = new(null!);
            PeerInfo peerB = new(null!);
            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();
            Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

            for (int i = 0; i <= AllowedInvalidResponses; i++)
            {
                feed.AnalyzeResponsePerPeer(AddRangeResult.EmptyRange, peerA);
            }

            snapProvider.Received(1).UpdatePivot();

            SyncResponseHandlingResult result = SyncResponseHandlingResult.OK;
            for (int i = 0; i <= AllowedInvalidResponses; i++)
            {
                result = feed.AnalyzeResponsePerPeer(AddRangeResult.EmptyRange, peerB);
            }

            Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK),
                "a different peer failing after the pivot update deserves its own stale-pivot benefit of the doubt");
            snapProvider.Received(2).UpdatePivot();
        }

        private const int AllowedInvalidResponses = Synchronization.SnapSync.SnapSyncFeed.AllowedInvalidResponses;
    }
}
