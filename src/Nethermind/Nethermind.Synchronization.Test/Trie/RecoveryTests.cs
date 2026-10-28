// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core.Test.Modules;
using Nethermind.History;
using Nethermind.Core.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
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
    private IContainer _container = null!;
    private SnapRangeRecovery _snapRecovery = null!;
    private NodeDataRecovery _nodeDataDataRecovery = null!;
    private ICodeRecovery _codeRecovery = null!;

    [SetUp]
    public void SetUp()
    {
        TrieNode node = new(new LeafData(Nibbles.BytesToNibbleBytes(Bytes.FromHexString("34000000000000000000000000000000000000000000000000000000000000")), new CappedArray<byte>([0])));
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
            .Returns(_ => Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _returnedRlp })));
        _peerEth66 = new(_syncPeerEth66);

        _snapSyncPeer = Substitute.For<ISnapSyncPeer>();
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(new AccountsAndProofs()
            {
                Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _returnedRlp }),
                PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(_fullPath, TestItem.GenerateIndexedAccount(0)) },
            }));
        _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _returnedRlp })));

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
        // Production hand-builds these two inside the IPathRecovery factory, so there is no
        // registration to resolve them from; ICodeRecovery is registered and comes from the container.
        _snapRecovery = new SnapRangeRecovery(_syncPeerPool, LimboLogs.Instance);
        _nodeDataDataRecovery = new NodeDataRecovery(_syncPeerPool, new NodeStorage(new MemDb()), LimboLogs.Instance);

        ConfigProvider configProvider = new();
        // Code healing rides the patricia store's recovery wiring; the flat layout has no equivalent.
        configProvider.GetConfig<IFlatDbConfig>().Enabled = false;
        configProvider.GetConfig<IPruningConfig>().Mode = PruningMode.Full;
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddSingleton(_syncPeerPool)
            .AddSingleton<IHistoryPruner>(Substitute.For<IHistoryPruner>())
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(1).TestObject)
            .Build();
        _codeRecovery = _container.Resolve<ICodeRecovery>();
    }

    [TearDown]
    public void TearDown()
    {
        _container?.Dispose();
        _syncPeerPool?.DisposeAsync();
    }

    [Test]
    public async Task can_recover_eth66()
    {
        using IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_nodeDataDataRecovery, _peerEth66);
        AssertRecoveredNode(response);
    }

    [Test]
    public async Task cannot_recover_eth66_no_peers()
    {
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_nodeDataDataRecovery, _peerEth67);
        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task cannot_recover_eth66_empty_response()
    {
        _syncPeerEth66.GetNodeData(Arg.Is<IReadOnlyList<Hash256>>(l => l.Contains(_hash)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IByteArrayList>(EmptyByteArrayList.Instance));
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_nodeDataDataRecovery, _peerEth66);
        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task cannot_recover_eth66_invalid_rlp()
    {
        _returnedRlp = new byte[] { 5, 6, 7 };
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_nodeDataDataRecovery, _peerEth66);
        Assert.That(response, Is.Null);
    }

    [Test]
    public void cannot_recover_eth66_partial_path()
    {
        // The first node resolves and is collected, then its child is unavailable, so the walk gives up mid-path.
        TrieNode extension = new(new ExtensionData { Key = [3], Value = TestItem.KeccakB });
        _returnedRlp = extension.RlpEncode(Substitute.For<ITrieNodeResolver>(), ref _path).ToArray()!;
        _hash = Keccak.Compute(_returnedRlp);

        AssertFailedRecoveryReturnsBuffer(_nodeDataDataRecovery, _peerEth66);
    }

    [Test]
    public async Task can_recover_eth67([Range(1, 2)] int peerCount)
    {
        using IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_snapRecovery, Eth67Peers(peerCount));
        AssertRecoveredNode(response);
    }

    [Test]
    public async Task cannot_recover_eth67_no_peers()
    {
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_snapRecovery, _peerEth66);
        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task can_recover_code_eth67([Range(1, 2)] int peerCount)
    {
        byte[]? response = await RecoverCode(Eth67Peers(peerCount));
        Assert.That(response, Is.EqualTo(_nodeRlp));
    }

    [Test]
    public async Task cannot_recover_code_eth67_no_peers()
    {
        byte[]? response = await RecoverCode(_peerEth66);
        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task cannot_recover_code_eth67_empty_response()
    {
        _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IByteArrayList>(EmptyByteArrayList.Instance));
        byte[]? response = await RecoverCode(_peerEth67);
        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task cannot_recover_code_eth67_hash_mismatch()
    {
        _returnedRlp = [5, 6, 7];
        byte[]? response = await RecoverCode(_peerEth67);
        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task cannot_recover_eth67_empty_response()
    {
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AccountsAndProofs>(null!));
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_snapRecovery, _peerEth66);
        Assert.That(response, Is.Null);
    }

    [Test]
    public void cannot_recover_eth67_unassemblable_proofs()
    {
        // Proofs that hash to nothing on the queried path leave the assembled node list empty.
        _returnedRlp = [5, 6, 7];
        AssertFailedRecoveryReturnsBuffer(_snapRecovery, _peerEth67);
    }

    [Test]
    public async Task cannot_recover_eth67_hash_mismatch()
    {
        _snapSyncPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { new byte[] { 5, 6, 7 } })));
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_nodeDataDataRecovery, _peerEth67);
        Assert.That(response, Is.Null);
    }

    private void AssertFailedRecoveryReturnsBuffer(IPathRecovery recovery, PeerInfo peer)
    {
        (TreePath, byte[])[] expected = SafeArrayPool<(TreePath, byte[])>.Shared.Rent(1);
        SafeArrayPool<(TreePath, byte[])>.Shared.Return(expected);

        Task<IOwnedReadOnlyList<(TreePath, byte[])>?> task = Recover(recovery, peer);
        // Completed mocks keep all rentals on this thread, where the pool reuses its last returned array.
        Assert.That(task.IsCompletedSuccessfully, Is.True);
        using IOwnedReadOnlyList<(TreePath, byte[])>? response = task.GetAwaiter().GetResult();

        (TreePath, byte[])[] actual = SafeArrayPool<(TreePath, byte[])>.Shared.Rent(1);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(response, Is.Null);
                Assert.That(actual, Is.SameAs(expected), "Recovery must return its rented node buffer.");
            }
        }
        finally
        {
            SafeArrayPool<(TreePath, byte[])>.Shared.Return(actual);
        }
    }

    private Task<IOwnedReadOnlyList<(TreePath, byte[])>?> Recover(IPathRecovery recovery, params PeerInfo[] peers)
    {
        SetupPeers(peers);
        return recovery.Recover(_rootHash, _storageHash, _path, _hash, _fullPath);
    }

    private void SetupPeers(PeerInfo[] peers)
    {
        _syncPeerPool.InitializedPeers.Returns(peers);
        int allocated = -1;
        _syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            AllocationContexts allocationContexts = (AllocationContexts)c[1];
            SyncPeerAllocation allocation = new(allocationContexts);
            // Hand the peers out in turn, so a multi-peer case allocates more than just the first.
            allocation.AllocatePeer(peers[Interlocked.Increment(ref allocated) % peers.Length]);
            return allocation;
        });
    }

    [Test]
    public async Task cannot_recover_code_when_no_peer_can_be_allocated()
    {
        // Peer allocation runs on an unbounded budget, so CodeRecovery has to bound the wait itself,
        // otherwise the code db read that blocks on it never returns.
        _syncPeerPool.InitializedPeers.Returns([]);
        _syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(c => NeverAllocates((CancellationToken)c[3]));

        byte[]? response = await _codeRecovery.Recover(_hash.ValueHash256).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(response, Is.Null);

        static async Task<SyncPeerAllocation> NeverAllocates(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return new SyncPeerAllocation(AllocationContexts.Snap);
        }
    }

    [Test]
    public async Task recovers_via_snap_when_node_data_recovery_throws()
    {
        // Wait.AnyWhere surfaces the first task to complete, so a faulting recovery used to discard the
        // result of the sibling racing it.
        INodeStorage throwingNodeStorage = Substitute.For<INodeStorage>();
        throwingNodeStorage.Get(Arg.Any<Hash256?>(), Arg.Any<TreePath>(), Arg.Any<ValueHash256>(), Arg.Any<ReadFlags>())
            .Returns<byte[]?>(_ => throw new InvalidOperationException("node storage unavailable"));

        PathNodeRecovery recovery = new(
            new NodeDataRecovery(_syncPeerPool, throwingNodeStorage, LimboLogs.Instance),
            _snapRecovery,
            LimboLogs.Instance);

        using IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(recovery, _peerEth67);
        AssertRecoveredNode(response);
    }

    [Test]
    public async Task cannot_recover_code_when_allocation_throws()
    {
        SetupThrowingAllocation();
        byte[]? response = await _codeRecovery.Recover(_hash.ValueHash256);
        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task cannot_recover_path_when_allocation_throws([Values("snap", "nodeData", "composite")] string recoveryKind)
    {
        IPathRecovery recovery = recoveryKind switch
        {
            "snap" => _snapRecovery,
            "nodeData" => _nodeDataDataRecovery,
            _ => new PathNodeRecovery(_nodeDataDataRecovery, _snapRecovery, LimboLogs.Instance),
        };

        SetupThrowingAllocation();
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await recovery.Recover(_rootHash, _storageHash, _path, _hash, _fullPath);
        Assert.That(response, Is.Null);
    }

    [Test]
    public async Task recovers_code_when_one_allocation_throws()
    {
        // A single faulted attempt must not discard the siblings that are about to succeed.
        // The surviving attempts must resolve asynchronously, otherwise every attempt is already
        // complete when Wait.AnyWhere runs and it is enumeration order that decides which is seen first.
        _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Yield();
                return (IByteArrayList)new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _returnedRlp });
            });

        int allocations = 0;
        _syncPeerPool.InitializedPeers.Returns([_peerEth67]);
        _syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                if (Interlocked.Increment(ref allocations) == 1) throw new InvalidOperationException("peer pool unavailable");
                SyncPeerAllocation allocation = new((AllocationContexts)c[1]);
                allocation.AllocatePeer(_peerEth67);
                return allocation;
            });

        byte[]? response = await _codeRecovery.Recover(_hash.ValueHash256);
        Assert.That(response, Is.EqualTo(_nodeRlp));
    }

    private void SetupThrowingAllocation()
    {
        _syncPeerPool.InitializedPeers.Returns([_peerEth67]);
        _syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<SyncPeerAllocation>(_ => throw new InvalidOperationException("peer pool unavailable"));
    }

    private void AssertRecoveredNode(IOwnedReadOnlyList<(TreePath, byte[])>? response)
    {
        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response![0].Item1, Is.EqualTo(_path), "path");
            Assert.That(response![0].Item2, Is.EqualTo(_nodeRlp), "rlp");
        }
    }

    private PeerInfo[] Eth67Peers(int count) => count == 1 ? [_peerEth67] : [_peerEth67, _peerEth67_2];

    private Task<byte[]?> RecoverCode(params PeerInfo[] peers)
    {
        SetupPeers(peers);
        return _codeRecovery.Recover(_hash.ValueHash256);
    }
}
