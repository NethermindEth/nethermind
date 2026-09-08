// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using System.Net;
using Nethermind.Core.Test;
using Nethermind.Trie;
using Nethermind.Core.Collections;

namespace Nethermind.Synchronization.Test.FastSync.SnapProtocolTests;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class StateSyncDispatcherTests
{
    private static IBlockTree _blockTree = null!;

    private ILogManager _logManager = null!;
#pragma warning disable NUnit1032
    private SyncPeerPool _pool = null!;
#pragma warning restore NUnit1032
    private StateSyncDispatcherTester _dispatcher = null!;

    private readonly PublicKey _publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");

    private const ulong ChainLength = 100;
    private static IBlockTree BlockTree => LazyInitializer.EnsureInitialized(ref _blockTree, static () => Build.A.BlockTree().OfChainLength(ChainLength).TestObject);

    [SetUp]
    public void Setup()
    {
        _logManager = LimboLogs.Instance;

        IBlockTree blockTree = CachedBlockTreeBuilder.OfLength((int)BlockTree.BestSuggestedHeader!.Number);
        ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
        _pool = new SyncPeerPool(blockTree, new NodeStatsManager(timerFactory, LimboLogs.Instance), new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), LimboLogs.Instance, 25);
        _pool.Start();

        _dispatcher =
            new StateSyncDispatcherTester(new StateSyncDownloader(_logManager), _pool);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _pool.DisposeAsync();
        await _dispatcher.DisposeAsync();
    }

    [Test]
    public async Task Eth66Peer_RunGetNodeData()
    {
        ISyncPeer peer = AddPeer(66);

        using StateSyncBatch batch = StateBatch(new StateSyncItem(Keccak.EmptyTreeHash, null, TreePath.Empty, NodeDataType.State));

        await _dispatcher.ExecuteDispatch(batch, 1);

        using IByteArrayList _ = await peer.ReceivedWithAnyArgs(1).GetNodeData(default!, default);
    }

    [Test]
    public async Task GroupMultipleStorageSlotsByAccount()
    {
        AddPeer(67, Substitute.For<ISnapSyncPeer>());

        StateSyncItem item01 = new(Keccak.EmptyTreeHash, null, TreePath.FromNibble([3]), NodeDataType.State);
        StateSyncItem item02 = new(Keccak.EmptyTreeHash, TestItem.KeccakA, TreePath.FromNibble([2]), NodeDataType.State);
        StateSyncItem item03 = new(Keccak.EmptyTreeHash, null, TreePath.FromNibble([1]), NodeDataType.State);
        StateSyncItem item04 = new(Keccak.EmptyTreeHash, TestItem.KeccakB, TreePath.FromNibble([21]), NodeDataType.State);
        StateSyncItem item05 = new(Keccak.EmptyTreeHash, TestItem.KeccakA, TreePath.FromNibble([1]), NodeDataType.State);
        StateSyncItem item06 = new(Keccak.EmptyTreeHash, TestItem.KeccakB, TreePath.FromNibble([22]), NodeDataType.State);

        using StateSyncBatch batch = StateBatch(item01, item02, item03, item04, item05, item06);

        await _dispatcher.ExecuteDispatch(batch, 1);

        Assert.That(batch.RequestedNodes, Is.Not.Null);
        Assert.That(batch.RequestedNodes!.Count, Is.EqualTo(6));
        Assert.That(batch.RequestedNodes[0], Is.EqualTo(item01));
        Assert.That(batch.RequestedNodes[1], Is.EqualTo(item03));
        Assert.That(batch.RequestedNodes[2], Is.EqualTo(item02));
        Assert.That(batch.RequestedNodes[3], Is.EqualTo(item05));
        Assert.That(batch.RequestedNodes[4], Is.EqualTo(item04));
        Assert.That(batch.RequestedNodes[5], Is.EqualTo(item06));
    }

    [Test]
    public async Task SnapPeer_FallsBackToNodeData_WhenTrieNodesCannotBeServed([Values] bool snapThrows)
    {
        ISnapSyncPeer snapPeer = Substitute.For<ISnapSyncPeer>();
        snapPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>()).Returns(
            snapThrows
                ? _ => throw new TimeoutException()
                : _ => Task.FromResult<IByteArrayList>(EmptyByteArrayList.Instance));
        ISyncPeer peer = AddPeer(66, snapPeer);

        using StateSyncBatch batch = StateBatch(new StateSyncItem(Keccak.EmptyTreeHash, null, TreePath.Empty, NodeDataType.State));

        await _dispatcher.ExecuteDispatch(batch, 1);

        await snapPeer.Received(1).GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>());
        using IByteArrayList _ = await peer.ReceivedWithAnyArgs(1).GetNodeData(default!, default);
    }

    [Test]
    public async Task SnapPeer_FallsBackToNodeData_WhenByteCodesAreEmpty()
    {
        ISnapSyncPeer snapPeer = Substitute.For<ISnapSyncPeer>();
        snapPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IByteArrayList>(EmptyByteArrayList.Instance));
        ISyncPeer peer = AddPeer(66, snapPeer);

        using StateSyncBatch batch = new(
            Keccak.OfAnEmptyString,
            NodeDataType.Code,
            [new StateSyncItem(Keccak.EmptyTreeHash, null, TreePath.Empty, NodeDataType.Code)]);

        await _dispatcher.ExecuteDispatch(batch, 1);

        await snapPeer.Received(1).GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>());
        using IByteArrayList _ = await peer.ReceivedWithAnyArgs(1).GetNodeData(default!, default);
    }

    [Test]
    public async Task SnapPeer_KeepsSnapResponse_WithoutFallingBack()
    {
        ArrayPoolList<byte[]> nodes = new(1) { new byte[] { 1, 2, 3 } };
        ISnapSyncPeer snapPeer = Substitute.For<ISnapSyncPeer>();
        snapPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(nodes)));
        ISyncPeer peer = AddPeer(66, snapPeer);

        using StateSyncBatch batch = StateBatch(new StateSyncItem(Keccak.EmptyTreeHash, null, TreePath.Empty, NodeDataType.State));

        await _dispatcher.ExecuteDispatch(batch, 1);

        Assert.That(batch.Responses, Is.Not.Null);
        Assert.That(batch.Responses!.Count, Is.EqualTo(1));
        await peer.DidNotReceiveWithAnyArgs().GetNodeData(default!, default);
    }

    private static StateSyncBatch StateBatch(params StateSyncItem[] items) =>
        new(Keccak.OfAnEmptyString, NodeDataType.State, items);

    private ISyncPeer AddPeer(byte protocolVersion, ISnapSyncPeer? snapPeer = null)
    {
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.Node.Returns(new Stats.Model.Node(_publicKey, new IPEndPoint(IPAddress.Broadcast, 30303)));
        peer.ProtocolVersion.Returns(protocolVersion);
        peer.IsInitialized.Returns(true);
        peer.TotalDifficulty.Returns(new Int256.UInt256(1_000_000_000));
        peer.HeadNumber.Returns(ChainLength - 1);

        if (snapPeer is not null)
        {
            peer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>()).Returns(x =>
            {
                x[1] = snapPeer;
                return true;
            });
        }

        _pool.AddPeer(peer);
        return peer;
    }
}
