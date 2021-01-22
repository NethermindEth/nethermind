//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Linq;
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
            Node a = new Node(TestItem.PublicKeyA, "127.0.0.1", 30303);
            Peer peerA = new Peer(a);

            Node b = new Node(TestItem.PublicKeyB, "127.0.0.1", 30303);
            Peer peerB = new Peer(b);

            Node c = new Node(TestItem.PublicKeyC, "127.0.0.1", 30303);
            Peer peerC = new Peer(c);

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
            Node a = new Node(TestItem.PublicKeyA, "127.0.0.1", 30303);
            Peer peerA = new Peer(a);

            Node b = new Node(TestItem.PublicKeyB, "127.0.0.1", 30304);
            Peer peerB = new Peer(b);

            Node c = new Node(TestItem.PublicKeyC, "127.0.0.1", 30305);
            Peer peerC = new Peer(c);
            
            Node d = new Node(TestItem.PublicKeyD, "127.0.0.1", 30306);
            Peer peerD = new Peer(d);
            Peer peerE = null;

            _statsManager.GetCurrentReputation(a).Returns(100);
            _statsManager.GetCurrentReputation(b).Returns(50);
            _statsManager.GetCurrentReputation(c).Returns(200);
            _statsManager.GetCurrentReputation(d).Returns(10);

            List<Peer> peers = new List<Peer> {peerA, peerB, peerC, peerD, peerE};
            
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
