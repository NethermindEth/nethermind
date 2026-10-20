// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.All)]
    public class PeerInfoAllocationTests
    {
        [TestCase("Nethermind/v1.10.71-0-13221de89-20211103/X64-Linux/5.0.5", AllocationContexts.All & ~AllocationContexts.Snap, ExpectedResult = true)]

        [TestCase("OpenEthereum/v3.3.0-rc.1-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.0-rc.2-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.0-rc.3-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/xdai-friends/v3.3.0-rc.8-stable-d8305c5-20210903/x86_64-linux-gnu", AllocationContexts.All & ~AllocationContexts.State, ExpectedResult = true)]
        [TestCase("OpenEthereum/xdai-friends/v3.3.0-rc.8-stable-d8305c5-20210903/x86_64-linux-gnu", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/xdai-friends/v3.3.0-rc.9-stable-d8305c5-20210903/x86_64-linux-gnu", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/xdai-friends/v3.3.0-rc.10-stable-d8305c5-20210903/x86_64-linux-gnu", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.2.5-stable-32d8b54-20210505/x86_64-linux-gnu/rustc1.51.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.1.1-stable-32d8b54-20210505/x86_64-linux-gnu/rustc1.51.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.0-rc.4-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.0-rc.7-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.0-rc.11-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.0-rc.15-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.1/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.2/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.0.0-stable-32d8b54-20210505/x86_64-linux-gnu/rustc1.51.0", AllocationContexts.State, ExpectedResult = true)]
        [TestCase("OpenEthereum/v3.3.3/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = true)]
        [TestCase("OpenEthereum/v3.3.4/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = true)]

        [TestCase("Nethermind/v1.10.71-0-13221de89-20211103/X64-Linux/5.0.5", AllocationContexts.Snap, ExpectedResult = true)]
        [TestCase("Geth/v1.10.23-stable-d901d853/linux-amd64/go1.18.5", AllocationContexts.Snap, ExpectedResult = true)]
        public bool SupportsAllocation(string versionString, AllocationContexts contexts)
        {
            PeerInfo peerInfo = new(SetupSyncPeer(versionString));
            return peerInfo.CanBeAllocated(contexts);
        }

        [TestCase(EthVersions.Eth70, ExpectedResult = false)]
        [TestCase(EthVersions.Eth71, ExpectedResult = true)]
        public bool SupportsBlockAccessListAllocation(byte protocolVersion)
        {
            ISyncPeer peer = SetupSyncPeer("Nethermind/v1.31.0/X64-Linux/10.0.0");
            peer.ProtocolVersion.Returns(protocolVersion);
            PeerInfo peerInfo = new(peer);

            return peerInfo.CanBeAllocated(AllocationContexts.BlockAccessLists);
        }

        [TestCase(EthVersions.Eth70, 256UL)]
        // A peer behind the highest requested block cannot have every list.
        [TestCase(EthVersions.Eth71, 199UL)]
        public void Blocks_request_allocation_filters_current_peer_that_cannot_serve_block_access_lists(byte currentProtocolVersion, ulong currentHeadNumber)
        {
            using BlocksRequest request = new();
            request.BlockAccessListsRequests.Dispose();
            request.BlockAccessListsRequests = BuildHeaders(100, 200);

            PeerInfo currentPeer = SetupPeerInfo("Nethermind/current", currentHeadNumber, currentProtocolVersion, 30303);
            PeerInfo highPreEth71Peer = SetupPeerInfo("Nethermind/high-eth70", 256, EthVersions.Eth70, 30304);
            PeerInfo highEth71Peer = SetupPeerInfo("Nethermind/high-eth71", 200, EthVersions.Eth71, 30305);
            INodeStatsManager nodeStatsManager = SetupNodeStatsManager();

            PeerInfo? allocated = new BlocksSyncPeerAllocationStrategyFactory()
                .Create(request)
                .Allocate(
                    currentPeer,
                    [currentPeer, highPreEth71Peer, highEth71Peer],
                    nodeStatsManager,
                    Substitute.For<IBlockTree>());

            Assert.That(allocated, Is.SameAs(highEth71Peer));
        }

        [TestCase(TransferSpeedType.Receipts)]
        [TestCase(TransferSpeedType.BlockAccessLists)]
        public void Blocks_request_allocation_ranks_peers_by_the_speed_of_the_requested_data(TransferSpeedType speedType)
        {
            PeerInfo fastBodies = SetupPeerInfo("Nethermind/fast-bodies", 256, EthVersions.Eth71, 30306);
            PeerInfo fastData = SetupPeerInfo("Nethermind/fast-data", 256, EthVersions.Eth71, 30307);
            INodeStats fastBodiesStats = Substitute.For<INodeStats>();
            fastBodiesStats.GetAverageTransferSpeed(TransferSpeedType.Bodies).Returns(10_000L);
            fastBodiesStats.GetAverageTransferSpeed(speedType).Returns(1L);
            INodeStats fastDataStats = Substitute.For<INodeStats>();
            fastDataStats.GetAverageTransferSpeed(TransferSpeedType.Bodies).Returns(100L);
            fastDataStats.GetAverageTransferSpeed(speedType).Returns(10_000L);
            INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();
            nodeStatsManager.GetOrAdd(fastBodies.SyncPeer.Node).Returns(fastBodiesStats);
            nodeStatsManager.GetOrAdd(fastData.SyncPeer.Node).Returns(fastDataStats);

            using BlocksRequest request = new();
            if (speedType == TransferSpeedType.Receipts)
            {
                request.ReceiptsRequests.Dispose();
                request.ReceiptsRequests = BuildHeaders(1, 1);
            }
            else
            {
                request.BlockAccessListsRequests.Dispose();
                request.BlockAccessListsRequests = BuildHeaders(1, 1);
            }

            // Allocation occasionally samples another peer to refresh its speed, so count the choices.
            IPeerAllocationStrategy strategy = new BlocksSyncPeerAllocationStrategyFactory().Create(request);
            int fastDataChosen = 0;
            for (int i = 0; i < 200; i++)
            {
                if (strategy.Allocate(null, [fastBodies, fastData], nodeStatsManager, Substitute.For<IBlockTree>()) == fastData) fastDataChosen++;
            }

            Assert.That(fastDataChosen, Is.GreaterThan(180));
        }

        public static IEnumerable OpenEthereumVersionTests
        {
            get
            {
                yield return new TestCaseData("Nethermind/v1.10.71-0-13221de89-20211103/X64-Linux/5.0.5", null, 0);
                yield return new TestCaseData("OpenEthereum/xdai-friends/v3.3.0-rc.8-stable-d8305c5-20210903/x86_64-linux-gnu", new Version(3, 3, 0), 8);
                yield return new TestCaseData("OpenEthereum/v3.3.0-rc.15-stable/x86_64-linux-musl/rustc1.47.0", new Version(3, 3, 0), 15);
                yield return new TestCaseData("OpenEthereum/v3.3.0-rc.13-stable/x86_64-linux-musl/rustc1.47.0", new Version(3, 3, 0), 13);
                yield return new TestCaseData("OpenEthereum/v3.2.5-stable-32d8b54-20210505/x86_64-linux-gnu/rustc1.51.0", new Version(3, 2, 5), 0);
                yield return new TestCaseData("OpenEthereum/pocket-foundation-1/v3.3.0-rc.7-stable/x86_64-linux-musl/rustc1.47.0", new Version(3, 3, 0), 7);
            }
        }

        [TestCaseSource(nameof(OpenEthereumVersionTests))]
        public void GetOpenEthereumVersion(string versionString, Version? expectedVersion = null, int expectedReleaseCandidate = 0)
        {
            ISyncPeer peer = SetupSyncPeer(versionString);

            Version? version = peer.GetOpenEthereumVersion(out int releaseCandidate);
            Assert.That(version, Is.EqualTo(expectedVersion));
            Assert.That(releaseCandidate, Is.EqualTo(expectedReleaseCandidate));
        }

        private static ArrayPoolList<BlockHeader> BuildHeaders(int start, int end)
        {
            ArrayPoolList<BlockHeader> headers = new(end - start + 1);
            for (int number = start; number <= end; number++)
            {
                headers.Add(Build.A.BlockHeader.WithNumber(number).TestObject);
            }

            return headers;
        }

        private static PeerInfo SetupPeerInfo(string versionString, ulong headNumber, byte protocolVersion, int port)
        {
            ISyncPeer peer = SetupSyncPeer(versionString);
            peer.Node.Returns(new Node(Build.A.PrivateKey.TestObject.PublicKey, "127.0.0.1", port));
            peer.IsInitialized.Returns(true);
            peer.HeadNumber.Returns(headNumber);
            peer.HeadHash.Returns(Build.A.BlockHeader.WithNumber(headNumber).TestObject.Hash!);
            peer.ProtocolVersion.Returns(protocolVersion);
            return new PeerInfo(peer);
        }

        private static INodeStatsManager SetupNodeStatsManager()
        {
            INodeStats nodeStats = Substitute.For<INodeStats>();
            nodeStats.GetAverageTransferSpeed(TransferSpeedType.Bodies).Returns((long?)null);

            INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();
            nodeStatsManager.GetOrAdd(Arg.Any<Node>()).Returns(nodeStats);
            return nodeStatsManager;
        }

        private static ISyncPeer SetupSyncPeer(string versionString)
        {
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.ClientId.Returns(versionString);
            peer.ClientType.Returns(Node.RecognizeClientType(versionString));
            return peer;
        }
    }
}
