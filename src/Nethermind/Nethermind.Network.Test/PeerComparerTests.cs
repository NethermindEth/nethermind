// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PeerComparerTests
    {
        private INodeStatsManager _statsManager;
        private PeerComparer _comparer;

        [SetUp]
        public void SetUp()
        {
            _statsManager = Substitute.For<INodeStatsManager>();
            _comparer = new PeerComparer();
        }

        [Test]
        public void Can_sort_by_Reputation()
        {
            Node a = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            Peer peerA = new(a);

            Node b = new(TestItem.PublicKeyB, "127.0.0.1", 30303);
            Peer peerB = new(b);

            Node c = new(TestItem.PublicKeyC, "127.0.0.1", 30303);
            Peer peerC = new(c);

            _statsManager.GetCurrentReputation(a).Returns(100);
            _statsManager.GetCurrentReputation(b).Returns(50);
            _statsManager.GetCurrentReputation(c).Returns(200);

            _statsManager.UpdateCurrentReputation(a, b, c);

            Assert.AreEqual(-1, _comparer.Compare(peerA, peerB));
            Assert.AreEqual(1, _comparer.Compare(peerA, peerC));
            Assert.AreEqual(1, _comparer.Compare(peerB, peerC));
            Assert.AreEqual(0, _comparer.Compare(peerA, peerA));
            Assert.AreEqual(0, _comparer.Compare(peerB, peerB));
            Assert.AreEqual(0, _comparer.Compare(peerC, peerC));
        }

        [Test]
        public void Can_sort()
        {
            Node a = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
            Peer peerA = new(a);

            Node b = new(TestItem.PublicKeyB, "127.0.0.1", 30304);
            Peer peerB = new(b);

            Node c = new(TestItem.PublicKeyC, "127.0.0.1", 30305);
            Peer peerC = new(c);

            Node d = new(TestItem.PublicKeyD, "127.0.0.1", 30306);
            Peer peerD = new(d);
            Peer peerE = null;

            _statsManager.GetCurrentReputation(a).Returns(100);
            _statsManager.GetCurrentReputation(b).Returns(50);
            _statsManager.GetCurrentReputation(c).Returns(200);
            _statsManager.GetCurrentReputation(d).Returns(10);

            List<Peer> peers = new() { peerA, peerB, peerC, peerD, peerE };

            _statsManager.UpdateCurrentReputation(peers);
            peers.Sort(_comparer);

            Assert.AreEqual(peerC, peers[0]);
            Assert.AreEqual(peerA, peers[1]);
            Assert.AreEqual(peerB, peers[2]);
            Assert.AreEqual(peerD, peers[3]);
            Assert.AreEqual(peerE, peers[4]);
        }
    }
}
