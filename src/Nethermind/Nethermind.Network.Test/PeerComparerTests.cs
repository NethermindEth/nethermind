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

            Assert.That(_comparer.Compare(peerA, peerB), Is.EqualTo(-1));
            Assert.That(_comparer.Compare(peerA, peerC), Is.EqualTo(1));
            Assert.That(_comparer.Compare(peerB, peerC), Is.EqualTo(1));
            Assert.That(_comparer.Compare(peerA, peerA), Is.EqualTo(0));
            Assert.That(_comparer.Compare(peerB, peerB), Is.EqualTo(0));
            Assert.That(_comparer.Compare(peerC, peerC), Is.EqualTo(0));
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

            Assert.That(peers[0], Is.EqualTo(peerC));
            Assert.That(peers[1], Is.EqualTo(peerA));
            Assert.That(peers[2], Is.EqualTo(peerB));
            Assert.That(peers[3], Is.EqualTo(peerD));
            Assert.That(peers[4], Is.EqualTo(peerE));
        }
    }
}
