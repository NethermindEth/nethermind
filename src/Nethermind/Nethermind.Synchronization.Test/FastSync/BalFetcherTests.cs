// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class BalFetcherTests
{
    private readonly Dictionary<ValueHash256, byte[]> _balByHash = [];
    private IBlockTree _blockTree = null!;
    private MemDb _balDb = null!;
    private BlockAccessListStore _balStore = null!;
    private ISyncPeerPool _pool = null!;
    private BalFetcher _fetcher = null!;

    [SetUp]
    public void SetUp()
    {
        _balByHash.Clear();
        _blockTree = Substitute.For<IBlockTree>();
        _balDb = new MemDb();
        _balStore = new BlockAccessListStore(_balDb);
        _pool = Substitute.For<ISyncPeerPool>();
        _fetcher = new BalFetcher(_pool, _blockTree, _balStore, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _balDb.Dispose();
        _pool?.DisposeAsync();
    }

    [Test]
    public async Task Fetches_missing_bals_via_snap2()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        BlockHeader to = Block(12, [0x03, 0x04]);
        AllocatePeer(Snap2Peer());

        bool result = await _fetcher.EnsureRange(from, to, default);

        Assert.That(result, Is.True);
        Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
        Assert.That(_balStore.Exists(12, to.Hash!), Is.True);
    }

    [Test]
    public async Task Falls_back_to_eth71_when_peer_has_no_snap2()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        AllocatePeer(Eth71Peer());

        bool result = await _fetcher.EnsureRange(from, b11, default);

        Assert.That(result, Is.True);
        Assert.That(_balStore.Exists(11, b11.Hash!), Is.True);
    }

    [Test]
    public async Task Returns_false_when_no_capable_peer()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        _pool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((SyncPeerAllocation)null!);

        bool result = await _fetcher.EnsureRange(from, b11, default);

        Assert.That(result, Is.False);
        Assert.That(_balStore.Exists(11, b11.Hash!), Is.False);
    }

    [Test]
    public async Task Rejects_bal_that_does_not_match_header_hash()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        // Peer serves garbage that won't hash to the header's BlockAccessListHash.
        _balByHash[b11.Hash!.ValueHash256] = [0xDE, 0xAD];
        AllocatePeer(Snap2Peer());

        bool result = await _fetcher.EnsureRange(from, b11, default);

        Assert.That(result, Is.False);
        Assert.That(_balStore.Exists(11, b11.Hash!), Is.False);
        _pool.Received().ReportWeakPeer(Arg.Any<PeerInfo>(), AllocationContexts.State);
    }

    [Test]
    public async Task Does_not_fetch_when_all_bals_already_present()
    {
        BlockHeader from = Block(10, bal: null);
        BlockHeader b11 = Block(11, [0x01, 0x02]);
        _balStore.Insert(11, b11.Hash!, _balByHash[b11.Hash!.ValueHash256]);

        bool result = await _fetcher.EnsureRange(from, b11, default);

        Assert.That(result, Is.True);
        await _pool.DidNotReceive().Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Registers a header at `number` and (when it has a BAL) records the RLP a peer should return for it.
    private BlockHeader Block(ulong number, byte[]? bal)
    {
        BlockHeaderBuilder builder = Build.A.BlockHeader.WithNumber(number);
        if (bal is not null) builder = builder.WithBlockAccessListHash(Keccak.Compute(bal));
        BlockHeader header = builder.TestObject;

        _blockTree.FindHeader(number).Returns(header);
        if (bal is not null) _balByHash[header.Hash!.ValueHash256] = bal;
        return header;
    }

    private void AllocatePeer(PeerInfo peer) =>
        _pool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => new SyncPeerAllocation(peer, (AllocationContexts)ci[1]));

    private PeerInfo Snap2Peer()
    {
        ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
        snap.SnapProtocolVersion.Returns(SnapVersions.Snap2);
        snap.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<ValueHash256> requested = ci.Arg<IReadOnlyList<ValueHash256>>();
                ArrayPoolList<byte[]> response = new(requested.Count);
                foreach (ValueHash256 hash in requested) response.Add(_balByHash.GetValueOrDefault(hash, []));
                return Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(response));
            });

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>())
            .Returns(ci => { ci[1] = snap; return true; });
        return new PeerInfo(syncPeer);
    }

    private PeerInfo Eth71Peer()
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(EthVersions.Eth71);
        syncPeer.GetBlockAccessLists(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<Hash256> requested = ci.Arg<IReadOnlyList<Hash256>>();
                ArrayPoolList<byte[]?> response = new(requested.Count);
                foreach (Hash256 hash in requested)
                    response.Add(_balByHash.TryGetValue(hash.ValueHash256, out byte[]? rlp) ? rlp : null);
                return Task.FromResult<IOwnedReadOnlyList<byte[]?>>(response);
            });
        return new PeerInfo(syncPeer);
    }
}
