// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Trie;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.Trie;

public class RecoveryTests
{
    private byte[] _nodeRlp = null!;
    private byte[] _returnedRlp = null!;

    private Hash256 _rootHash = null!;
    private Hash256? _storageHash = null!;
    private TreePath _path = TreePath.Empty;
    private Hash256 _fullPath = null!;
    private Hash256 _hash = null!;

    private ISyncPeer _syncPeerEth66 = null!;
    private PeerInfo _peerEth66 = null!;
    private PeerInfo _peerEth67 = null!;
    private PeerInfo _peerEth67_2 = null!;
    private ISnapSyncPeer _snapSyncPeer = null!;
    private ISyncPeerPool _syncPeerPool = null!;
    private SnapRangeRecovery _snapRecovery = null!;
    private NodeDataRecovery _nodeDataDataRecovery = null!;

    [SetUp]
    public void SetUp()
    {
        TrieNode node = new TrieNode(new LeafData(Nibbles.BytesToNibbleBytes(Bytes.FromHexString("34000000000000000000000000000000000000000000000000000000000000")), new CappedArray<byte>([0])));
        _path = TreePath.FromNibble([1, 2]);
        _nodeRlp = node.RlpEncode(Substitute.For<ITrieNodeResolver>(), ref _path).ToArray()!;
        _returnedRlp = _nodeRlp;

        _rootHash = TestItem.KeccakA;
        _storageHash = null;
        _fullPath = new Hash256("1234000000000000000000000000000000000000000000000000000000000000");
        _hash = Keccak.Compute(_nodeRlp);

        _syncPeerEth66 = Substitute.For<ISyncPeer>();
        _syncPeerEth66.ProtocolVersion.Returns(EthVersions.Eth66);
        _syncPeerEth66.GetNodeData(Arg.Is<IReadOnlyList<Hash256>>(l => l.Contains(_hash)), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IOwnedReadOnlyList<byte[]>>(new ArrayPoolList<byte[]>(1) { _returnedRlp }));
        _peerEth66 = new(_syncPeerEth66);

        _snapSyncPeer = Substitute.For<ISnapSyncPeer>();
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(new AccountsAndProofs()
            {
                Proofs = new ArrayPoolList<byte[]>(1) { _returnedRlp },
                PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(_fullPath, TestItem.GenerateIndexedAccount(0)) },
            }));

        ISyncPeer MakeEth67Peer()
        {
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.ProtocolVersion.Returns(EthVersions.Eth67);
            peer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>())
                .Returns(c =>
                {
                    c[1] = _snapSyncPeer;
                    return true;
                });

            return peer;
        }
        _peerEth67 = new(MakeEth67Peer());
        _peerEth67_2 = new(MakeEth67Peer());

        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _snapRecovery = new SnapRangeRecovery(_syncPeerPool, LimboLogs.Instance);
        _nodeDataDataRecovery = new NodeDataRecovery(_syncPeerPool, new NodeStorage(new MemDb()), LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _syncPeerPool?.DisposeAsync();
    }

    [Test]
    public async Task can_recover_eth66()
    {
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_nodeDataDataRecovery, _peerEth66);
        response![0].Should().Be((_path, _nodeRlp));
    }

    [Test]
    public async Task cannot_recover_eth66_no_peers()
    {
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_nodeDataDataRecovery, _peerEth67);
        response.Should().BeNull();
    }

    [Test]
    public async Task cannot_recover_eth66_empty_response()
    {
        _syncPeerEth66.GetNodeData(Arg.Is<IReadOnlyList<Hash256>>(l => l.Contains(_hash)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IOwnedReadOnlyList<byte[]>>(ArrayPoolList<byte[]>.Empty()));
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_nodeDataDataRecovery, _peerEth66);
        response.Should().BeNull();
    }

    [Test]
    public async Task cannot_recover_eth66_invalid_rlp()
    {
        _returnedRlp = new byte[] { 5, 6, 7 };
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_nodeDataDataRecovery, _peerEth66);
        response.Should().BeNull();
    }

    [Test]
    public async Task can_recover_eth67()
    {
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_snapRecovery, _peerEth67);
        response![0].Should().Be((_path, _nodeRlp));
    }

    [Test]
    public async Task can_recover_eth67_2_peer()
    {
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_snapRecovery, _peerEth67, _peerEth67_2);
        response![0].Should().Be((_path, _nodeRlp));
    }

    [Test]
    public async Task cannot_recover_eth67_no_peers()
    {
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_snapRecovery, _peerEth66);
        response.Should().BeNull();
    }

    [Test]
    public async Task cannot_recover_eth67_empty_response()
    {
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AccountsAndProofs>(null!));
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_snapRecovery, _peerEth66);
        response.Should().BeNull();
    }

    private Task<IOwnedReadOnlyList<(TreePath, byte[])>?> Recover(IPathRecovery recovery, params PeerInfo[] peers)
    {
        _syncPeerPool.InitializedPeers.Returns(peers);
        _syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            AllocationContexts allocationContexts = (AllocationContexts)c[1];
            var alloc = new SyncPeerAllocation(peers[0], allocationContexts);
            return alloc;
        });
        return recovery.Recover(_rootHash, _storageHash, _path, _hash, _fullPath);
    }
}
