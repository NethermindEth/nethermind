// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Snap;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.Trie;

public class RecoveryTests
{
    private byte[] _keyRlp = null!;
    private byte[] _returnedRlp = null!;
    private Keccak _key = null!;
    private ISyncPeer _syncPeerEth66 = null!;
    private PeerInfo _peerEth66 = null!;
    private ISyncPeer _syncPeerEth67 = null!;
    private PeerInfo _peerEth67 = null!;
    private ISnapSyncPeer _snapSyncPeer = null!;
    private GetTrieNodesRequest _snapRequest = null!;
    private ISyncPeerPool _syncPeerPool = null!;
    private SnapTrieNodeRecovery _snapRecovery = null!;
    private GetNodeDataTrieNodeRecovery _nodeDataRecovery = null!;

    [SetUp]
    public void SetUp()
    {
        _returnedRlp = _keyRlp = new byte[] { 1, 2, 3 };
        _key = Keccak.Compute(_keyRlp);
        _syncPeerEth66 = Substitute.For<ISyncPeer>();
        _syncPeerEth66.ProtocolVersion.Returns(EthVersions.Eth66);
        _syncPeerEth66.GetNodeData(Arg.Is<IReadOnlyList<Keccak>>(l => l.Contains(_key)), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new[] { _returnedRlp }));
        _peerEth66 = new(_syncPeerEth66);

        _snapSyncPeer = Substitute.For<ISnapSyncPeer>();
        _snapSyncPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(new[] { _returnedRlp }));
        _syncPeerEth67 = Substitute.For<ISyncPeer>();
        _syncPeerEth67.ProtocolVersion.Returns(EthVersions.Eth67);
        _syncPeerEth67.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>())
            .Returns(c =>
            {
                c[1] = _snapSyncPeer;
                return true;
            });
        _peerEth67 = new(_syncPeerEth67);

        _snapRequest = new GetTrieNodesRequest { AccountAndStoragePaths = Array.Empty<PathGroup>() };
        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _snapRecovery = new SnapTrieNodeRecovery(_syncPeerPool, LimboLogs.Instance);
        _nodeDataRecovery = new GetNodeDataTrieNodeRecovery(_syncPeerPool, LimboLogs.Instance);
    }

    [Test]
    public async Task can_recover_eth66()
    {
        byte[]? rlp = await Recover(_nodeDataRecovery, new List<Keccak> { _key }, _peerEth66);
        rlp.Should().BeEquivalentTo(_keyRlp);
    }

    [Test]
    public async Task cannot_recover_eth66_no_peers()
    {
        byte[]? rlp = await Recover(_nodeDataRecovery, new List<Keccak> { _key }, _peerEth67);
        rlp.Should().BeNull();
    }

    [Test]
    public async Task cannot_recover_eth66_empty_response()
    {
        _syncPeerEth66.GetNodeData(Arg.Is<IReadOnlyList<Keccak>>(l => l.Contains(_key)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<byte[]>()));
        byte[]? rlp = await Recover(_nodeDataRecovery, new List<Keccak> { _key }, _peerEth66);
        rlp.Should().BeNull();
    }

    [Test]
    public async Task cannot_recover_eth66_invalid_rlp()
    {
        _returnedRlp = new byte[] { 5, 6, 7 };
        byte[]? rlp = await Recover(_nodeDataRecovery, new List<Keccak> { _key }, _peerEth66);
        rlp.Should().BeNull();
    }

    [Test]
    public async Task can_recover_eth67()
    {
        byte[]? rlp = await Recover(_snapRecovery, _snapRequest, _peerEth67);
        rlp.Should().BeEquivalentTo(_keyRlp);
    }

    [Test]
    public async Task cannot_recover_eth67_no_peers()
    {
        byte[]? rlp = await Recover(_snapRecovery, _snapRequest, _peerEth66);
        rlp.Should().BeNull();
    }

    [Test]
    public async Task cannot_recover_eth67_empty_response()
    {
        _snapSyncPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<byte[]>()));
        byte[]? rlp = await Recover(_snapRecovery, _snapRequest, _peerEth67);
        rlp.Should().BeNull();
    }

    private Task<byte[]?> Recover<T, TRequest>(T recovery, TRequest request, params PeerInfo[] peers) where T : ITrieNodeRecovery<TRequest>
    {
        _syncPeerPool.InitializedPeers.Returns(peers);
        return recovery.Recover(_key, request);
    }
}
