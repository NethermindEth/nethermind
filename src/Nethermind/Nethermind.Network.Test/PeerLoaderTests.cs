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
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PeerLoaderTests
    {
        private NetworkConfig _networkConfig;
        private DiscoveryConfig _discoveryConfig;
        private INodeStatsManager _statsManager;
        private INetworkStorage _peerStorage;
        private PeerLoader _loader;

        [SetUp]
        public void SetUp()
        {
            _networkConfig = new NetworkConfig();
            _discoveryConfig = new DiscoveryConfig();
            _statsManager = Substitute.For<INodeStatsManager>();
            _peerStorage = Substitute.For<INetworkStorage>();
            _loader = new PeerLoader(_networkConfig, _discoveryConfig, _statsManager, _peerStorage, LimboLogs.Instance);
        }

        [Test]
        public void When_no_peers_then_no_peers_nada_zero()
        {
            List<Peer> peers = _loader.LoadPeers();
            Assert.AreEqual(0, peers.Count);
        }

        private const string enodesString = enode1String + "," + enode2String;
        private const string enode1String = "enode://22222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222@51.141.78.53:30303";
        private const string enode2String = "enode://1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111b@52.141.78.53:30303";

        [Test]
        public void Can_load_static_nodes()
        {
            _networkConfig.StaticPeers = enodesString;
            List<Peer> peers = _loader.LoadPeers();
            Assert.AreEqual(2, peers.Count);
            foreach (Peer peer in peers)
            {
                Assert.True(peer.Node.IsStatic);
            }
        }

        [Test]
        public void Can_load_bootnodes()
        {
            _discoveryConfig.Bootnodes = enodesString;
            List<Peer> peers = _loader.LoadPeers();
            Assert.AreEqual(2, peers.Count);
            foreach (Peer peer in peers)
            {
                Assert.True(peer.Node.IsBootnode);
            }
        }

        [Test]
        public void Can_load_persisted()
        {
            _peerStorage.GetPersistedNodes().Returns(new[] {new NetworkNode(enode1String), new NetworkNode(enode2String)});
            List<Peer> peers = _loader.LoadPeers();
            Assert.AreEqual(2, peers.Count);
            foreach (Peer peer in peers)
            {
                Assert.False(peer.Node.IsBootnode);
                Assert.False(peer.Node.IsStatic);
            }
        }
    }
}
