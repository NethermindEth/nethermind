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

            ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(selector, snapProvider, LimboLogs.Instance);

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

            var result = feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);

            Assert.AreEqual(SyncResponseHandlingResult.LesserQuality, result);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            Assert.AreEqual(SyncResponseHandlingResult.LesserQuality, result);
        }

        [Test]
        public void Test02()
        {
            PeerInfo peer1 = new(null!);
            PeerInfo peer2 = new(null!);

            ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(selector, snapProvider, LimboLogs.Instance);

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

            var result = feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);

            Assert.AreEqual(SyncResponseHandlingResult.LesserQuality, result);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            Assert.AreEqual(SyncResponseHandlingResult.OK, result);
        }

        [Test]
        public void Test03()
        {
            PeerInfo peer1 = new(null!);
            PeerInfo peer2 = new(null!);

            ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(selector, snapProvider, LimboLogs.Instance);

            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer2);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
            var result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
            Assert.AreEqual(SyncResponseHandlingResult.OK, result);

            snapProvider.Received(1).UpdatePivot();
        }

        [Test]
        public void Test04()
        {
            PeerInfo peer1 = new(null!);

            ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
            ISnapProvider snapProvider = Substitute.For<ISnapProvider>();

            Synchronization.SnapSync.SnapSyncFeed feed = new(selector, snapProvider, LimboLogs.Instance);

            for (int i = 0; i < 200; i++)
            {
                feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
            }
        }
    }
}
