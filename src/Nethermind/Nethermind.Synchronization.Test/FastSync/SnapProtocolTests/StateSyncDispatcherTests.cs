using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using System.Net;
using FluentAssertions;

namespace Nethermind.Synchronization.Test.FastSync.SnapProtocolTests
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class StateSyncDispatcherTests
    {
        private static IBlockTree _blockTree;
        private ILogManager _logManager;
        SyncPeerPool _pool;
        StateSyncDispatcherTester _dispatcher;

        private PublicKey _publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");

        private static IBlockTree BlockTree => LazyInitializer.EnsureInitialized(ref _blockTree, () => Build.A.BlockTree().OfChainLength(100).TestObject);

        [SetUp]
        public void Setup()
        {
            _logManager = LimboLogs.Instance;

            BlockTree blockTree = Build.A.BlockTree().OfChainLength((int)BlockTree.BestSuggestedHeader!.Number).TestObject;
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            _pool = new SyncPeerPool(blockTree, new NodeStatsManager(timerFactory, LimboLogs.Instance), new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), 25, LimboLogs.Instance);
            _pool.Start();

            var feed = Substitute.For<ISyncFeed<StateSyncBatch>>();
            _dispatcher =
                new StateSyncDispatcherTester(feed, _pool, new StateSyncAllocationStrategyFactory(), _logManager);
        }

        [Test]
        public async Task Eth66Peer_RunGetNodeData()
        {
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.Node.Returns(new Stats.Model.Node(_publicKey, new IPEndPoint(IPAddress.Broadcast, 30303)));
            peer.IsInitialized.Returns(true);
            peer.TotalDifficulty.Returns(new Int256.UInt256(1_000_000_000));
            _pool.AddPeer(peer);

            StateSyncBatch batch = new StateSyncBatch(Keccak.OfAnEmptyString, NodeDataType.State,
                new StateSyncItem[] { new StateSyncItem(Keccak.EmptyTreeHash, Array.Empty<byte>(), Array.Empty<byte>(), NodeDataType.State) });

            await _dispatcher.ExecuteDispatch(batch, 1);

            await peer.ReceivedWithAnyArgs(1).GetNodeData(default, default);
        }

        [Test]
        [Ignore("ETH67 not implemented yet")]
        public async Task GroupMultipleStorageSlotsByAccount()
        {
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.Node.Returns(new Stats.Model.Node(_publicKey, new IPEndPoint(IPAddress.Broadcast, 30303)));
            peer.IsInitialized.Returns(true);
            peer.TotalDifficulty.Returns(new Int256.UInt256(1_000_000_000));
            ISnapSyncPeer snapPeer = Substitute.For<ISnapSyncPeer>();
            peer.TryGetSatelliteProtocol("snap", out Arg.Any<ISnapSyncPeer>()).Returns(
                x =>
                {
                    x[1] = snapPeer;
                    return true;
                });
            //TODO: configure peer SNAP only (no ETH66 support)
            _pool.AddPeer(peer);

            var item01 = new StateSyncItem(Keccak.EmptyTreeHash, null, new byte[] { 3 }, NodeDataType.State);
            var item02 = new StateSyncItem(Keccak.EmptyTreeHash, new byte[] { 11 }, new byte[] { 2 }, NodeDataType.State);
            var item03 = new StateSyncItem(Keccak.EmptyTreeHash, null, new byte[] { 1 }, NodeDataType.State);
            var item04 = new StateSyncItem(Keccak.EmptyTreeHash, new byte[] { 22 }, new byte[] { 21 }, NodeDataType.State);
            var item05 = new StateSyncItem(Keccak.EmptyTreeHash, new byte[] { 11 }, new byte[] { 1 }, NodeDataType.State);
            var item06 = new StateSyncItem(Keccak.EmptyTreeHash, new byte[] { 22 }, new byte[] { 22 }, NodeDataType.State);

            StateSyncBatch batch = new StateSyncBatch(Keccak.OfAnEmptyString, NodeDataType.State, new StateSyncItem[] {
                item01, item02, item03, item04, item05, item06
            });

            await _dispatcher.ExecuteDispatch(batch, 1);

            batch.RequestedNodes.Count().Should().Be(6);
            batch.RequestedNodes[0].Should().Be(item01);
            batch.RequestedNodes[1].Should().Be(item03);
            batch.RequestedNodes[2].Should().Be(item02);
            batch.RequestedNodes[3].Should().Be(item05);
            batch.RequestedNodes[4].Should().Be(item04);
            batch.RequestedNodes[5].Should().Be(item06);
        }
    }
}
