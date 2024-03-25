// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.GethStyle.JavaScript;
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
    private Hash256 _key = null!;
    private ISyncPeer _syncPeerEth66 = null!;
    private PeerInfo _peerEth66 = null!;
    private PeerInfo _peerEth67 = null!;
    private PeerInfo _peerEth67_2 = null!;
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
        _syncPeerEth66.GetNodeData(Arg.Is<IReadOnlyList<Hash256>>(l => l.Contains(_key)), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IOwnedReadOnlyList<byte[]>>(new ArrayPoolList<byte[]>(1) { _returnedRlp }));
        _peerEth66 = new(_syncPeerEth66);

        _snapSyncPeer = Substitute.For<ISnapSyncPeer>();
        _snapSyncPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                GetTrieNodesRequest request = (GetTrieNodesRequest)c[0];
                _ = request.AccountAndStoragePaths.Count; // Trigger dispose exception if disposed
                request.AccountAndStoragePaths.Dispose();
                return Task.FromResult<IOwnedReadOnlyList<byte[]>>(new ArrayPoolList<byte[]>(1) { _returnedRlp });
            });

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

        _snapRequest = new GetTrieNodesRequest
        {
            AccountAndStoragePaths = new ArrayPoolList<PathGroup>(1)
            {
                new() { Group = [TestItem.KeccakA.BytesToArray()] }
            }
        };
        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _snapRecovery = new SnapTrieNodeRecovery(_syncPeerPool, LimboLogs.Instance);
        _nodeDataRecovery = new GetNodeDataTrieNodeRecovery(_syncPeerPool, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _syncPeerPool?.Dispose();

    [Test]
    public async Task can_recover_eth66()
    {
        byte[]? rlp = await Recover(_nodeDataRecovery, new List<Hash256> { _key }, _peerEth66);
        rlp.Should().BeEquivalentTo(_keyRlp);
    }

    [Test]
    public async Task cannot_recover_eth66_no_peers()
    {
        byte[]? rlp = await Recover(_nodeDataRecovery, new List<Hash256> { _key }, _peerEth67);
        rlp.Should().BeNull();
    }

    [Test]
    public async Task cannot_recover_eth66_empty_response()
    {
        _syncPeerEth66.GetNodeData(Arg.Is<IReadOnlyList<Hash256>>(l => l.Contains(_key)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IOwnedReadOnlyList<byte[]>>(ArrayPoolList<byte[]>.Empty()));
        byte[]? rlp = await Recover(_nodeDataRecovery, new List<Hash256> { _key }, _peerEth66);
        rlp.Should().BeNull();
    }

    [Test]
    public async Task cannot_recover_eth66_invalid_rlp()
    {
        _returnedRlp = new byte[] { 5, 6, 7 };
        byte[]? rlp = await Recover(_nodeDataRecovery, new List<Hash256> { _key }, _peerEth66);
        rlp.Should().BeNull();
    }

    [Test]
    public async Task can_recover_eth67()
    {
        byte[]? rlp = await Recover(_snapRecovery, _snapRequest, _peerEth67);
        rlp.Should().BeEquivalentTo(_keyRlp);
    }

    [Test]
    public async Task can_recover_eth67_2_peer()
    {
        byte[]? rlp = await Recover(_snapRecovery, _snapRequest, _peerEth67, _peerEth67_2);
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
            .Returns(Task.FromResult<IOwnedReadOnlyList<byte[]>>(ArrayPoolList<byte[]>.Empty()));
        byte[]? rlp = await Recover(_snapRecovery, _snapRequest, _peerEth67);
        rlp.Should().BeNull();
    }

    private Task<byte[]?> Recover<T, TRequest>(T recovery, TRequest request, params PeerInfo[] peers) where T : ITrieNodeRecovery<TRequest>
    {
        _syncPeerPool.InitializedPeers.Returns(peers);
        return recovery.Recover(_key, request);
    }
}
