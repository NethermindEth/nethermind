// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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
using Nethermind.Core.Test.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Stats.Model;
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
    private INodeStatsManager _stats = null!;
    private ManualTimeProvider _timeProvider = null!;

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
        _syncPeerEth66.Node.Returns(new Node(TestItem.PublicKeyC, "127.0.0.1", 30305));
        _syncPeerEth66.ProtocolVersion.Returns(EthVersions.Eth66);
        _syncPeerEth66.GetNodeData(Arg.Is<IReadOnlyList<Hash256>>(l => l.Contains(_hash)), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _returnedRlp })));
        _peerEth66 = new(_syncPeerEth66);

        _snapSyncPeer = Substitute.For<ISnapSyncPeer>();
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(new AccountsAndProofs()
            {
                Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _returnedRlp }),
                PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(_fullPath, TestItem.GenerateIndexedAccount(0).ToStruct()) },
            }));
        _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _returnedRlp })));

        ISyncPeer MakeEth67Peer(Node node)
        {
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.ProtocolVersion.Returns(EthVersions.Eth67);
            peer.Node.Returns(node);
            peer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>())
                .Returns(c =>
                {
                    c[1] = _snapSyncPeer;
                    return true;
                });

            return peer;
        }
        _peerEth67 = new(MakeEth67Peer(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303)));
        _peerEth67_2 = new(MakeEth67Peer(new Node(TestItem.PublicKeyB, "127.0.0.1", 30304)));

        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        // Production hand-builds these two inside the IPathRecovery factory, so there is no
        // registration to resolve them from; ICodeRecovery is registered and comes from the container.
        _stats = Substitute.For<INodeStatsManager>();
        _timeProvider = new ManualTimeProvider();
        _snapRecovery = new SnapRangeRecovery(_syncPeerPool, LimboLogs.Instance, _stats) { TimeProvider = _timeProvider };
        _nodeDataDataRecovery = new NodeDataRecovery(_syncPeerPool, new NodeStorage(new MemDb()), LimboLogs.Instance);

        _container = CreateContainer();
        _codeRecovery = _container.Resolve<ICodeRecovery>();
        ((CodeRecovery)_codeRecovery).TimeProvider = _timeProvider;
        _stats.ClearReceivedCalls();
    }

    private IContainer CreateContainer(INodeStorage? nodeStorage = null)
    {
        ConfigProvider configProvider = new();
        // Code healing rides the patricia store's recovery wiring; the flat layout has no equivalent.
        configProvider.GetConfig<IFlatDbConfig>().Enabled = false;
        configProvider.GetConfig<IPruningConfig>().Mode = PruningMode.Full;
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddSingleton(_syncPeerPool)
            .AddSingleton(_stats)
            .AddSingleton<IHistoryPruner>(Substitute.For<IHistoryPruner>())
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(1).TestObject);
        if (nodeStorage is not null) builder.AddSingleton(nodeStorage);
        return builder.Build();
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
        AssertSpeedSamples(1);
    }

    [Test]
    public async Task cannot_recover_eth67_no_peers()
    {
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_snapRecovery, _peerEth66);
        Assert.That(response, Is.Null);
        AssertSpeedSamples(0);
    }

    [Test]
    public async Task can_recover_code_eth67([Range(1, 2)] int peerCount)
    {
        byte[]? response = await RecoverCode(Eth67Peers(peerCount));
        Assert.That(response, Is.EqualTo(_nodeRlp));
        AssertSpeedSamples(1);
    }

    [Test]
    public async Task cannot_recover_code_eth67_no_peers()
    {
        byte[]? response = await RecoverCode(_peerEth66);
        Assert.That(response, Is.Null);
        AssertSpeedSamples(0);
    }

    [Test]
    public async Task cannot_recover_code_eth67_empty_response()
    {
        _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IByteArrayList>(EmptyByteArrayList.Instance));
        byte[]? response = await RecoverCode(_peerEth67);
        Assert.That(response, Is.Null);
        AssertSpeedSamples(0);
    }

    [Test]
    public async Task cannot_recover_code_eth67_hash_mismatch()
    {
        _returnedRlp = [5, 6, 7];
        byte[]? response = await RecoverCode(_peerEth67);
        Assert.That(response, Is.Null);
        AssertSpeedSamples(0);
    }

    [Test]
    public async Task cannot_recover_eth67_empty_response()
    {
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new AccountsAndProofs()));
        IOwnedReadOnlyList<(TreePath, byte[])>? response = await Recover(_snapRecovery, _peerEth67);
        Assert.That(response, Is.Null);
        AssertSpeedSamples(0);
    }

    [Test]
    public void cannot_recover_eth67_unassemblable_proofs()
    {
        // Proofs that hash to nothing on the queried path leave the assembled node list empty.
        _returnedRlp = [5, 6, 7];
        AssertFailedRecoveryReturnsBuffer(_snapRecovery, _peerEth67);
    }

    [Test]
    public void Malformed_hash_anchored_range_returns_buffer_and_records_no_sample()
    {
        _returnedRlp = [0xc0];
        _hash = Keccak.Compute(_returnedRlp);
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
                AssertSpeedSamples(0);
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
        AssertSpeedSamples(0);

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
        AssertSpeedSamples(1);
    }

    [Test]
    public async Task cannot_recover_code_when_allocation_throws()
    {
        SetupThrowingAllocation();
        byte[]? response = await _codeRecovery.Recover(_hash.ValueHash256);
        Assert.That(response, Is.Null);
        AssertSpeedSamples(0);
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
        AssertSpeedSamples(0);
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
        AssertSpeedSamples(1);
    }

    private void SetupThrowingAllocation()
    {
        _syncPeerPool.InitializedPeers.Returns([_peerEth67]);
        _syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<SyncPeerAllocation>(_ => throw new InvalidOperationException("peer pool unavailable"));
    }

    [Test]
    public async Task Production_path_recovery_reports_only_rooted_winner_bytes([Values] bool valid)
    {
        if (!valid)
        {
            _returnedRlp = (byte[])_nodeRlp.Clone();
            _returnedRlp[^1] ^= 1;
        }
        SetupPeers([_peerEth67]);
        IPathRecovery recovery = _container.Resolve<IPathRecovery>();
        using IOwnedReadOnlyList<(TreePath, byte[])>? response = await recovery.Recover(_rootHash, _storageHash, _path, _hash, _fullPath);
        if (valid) AssertRecoveredNode(response);
        else Assert.That(response, Is.Null);
        if (valid)
        {
            AssertProductionSpeedSample(_peerEth67);
        }
        else AssertSpeedSamples(0);
        if (valid) Assert.That(response!.Sum(node => node.Item2.Length), Is.EqualTo(_nodeRlp.Length));
    }

    [Test]
    [NonParallelizable]
    public void Production_composite_reports_only_accepted_snap_results_and_disposes_losers([Values] bool snapWins)
    {
        using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(10));
        using RecoveryTaskScheduler scheduler = new();
        TaskCompletionSource releaseSnap = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseNode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource nodesDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int pendingNodes = 0;
        int disposedNodes = 0;
        List<CountedBytes> proofs = [];
        List<CountedBytes> nodeResponses = [];
        INodeStorage nodeStorage = Substitute.For<INodeStorage>();
        nodeStorage.Get(Arg.Any<Hash256?>(), Arg.Any<TreePath>(), Arg.Any<ValueHash256>(), Arg.Any<ReadFlags>())
            .Returns(_ =>
            {
                if (!snapWins) releaseSnap.TrySetResult();
                return snapWins ? null : _nodeRlp;
            });
        _snapSyncPeer.SnapProtocolVersion.Returns(SnapVersions.Snap1);
        _snapSyncPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (++pendingNodes == 2) releaseSnap.TrySetResult();
                await releaseNode.Task;
                CountedBytes response = new(_nodeRlp)
                {
                    OnDisposed = () =>
                    {
                        if (++disposedNodes == 2) nodesDisposed.TrySetResult();
                    }
                };
                nodeResponses.Add(response);
                return (IByteArrayList)response;
            });
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Assert.That(releaseSnap.Task.IsCompletedSuccessfully, Is.True);
                CountedBytes proof = new(_nodeRlp);
                proofs.Add(proof);
                return Task.FromResult(new AccountsAndProofs { Proofs = proof });
            });
        _syncPeerPool.InitializedPeers.Returns([_peerEth67, _peerEth67_2]);
        _syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                AllocationContexts context = call.Arg<AllocationContexts>();
                SyncPeerAllocation allocation = new(context);
                allocation.AllocatePeer(context == AllocationContexts.Snap ? _peerEth67_2 : _peerEth67);
                return allocation;
            });
        using IContainer container = CreateContainer(nodeStorage);
        IPathRecovery recovery = container.Resolve<IPathRecovery>();
        (TreePath, byte[])[][] seeded = new (TreePath, byte[])[4][];
        for (int i = 0; i < seeded.Length; i++) seeded[i] = SafeArrayPool<(TreePath, byte[])>.Shared.Rent(1);
        foreach ((TreePath, byte[])[] buffer in seeded) SafeArrayPool<(TreePath, byte[])>.Shared.Return(buffer);

        Task<Task<IOwnedReadOnlyList<(TreePath, byte[])>?>> started = Task.Factory.StartNew(
            () => recovery.Recover(_rootHash, _storageHash, _path, _hash, _fullPath, watchdog.Token),
            watchdog.Token, TaskCreationOptions.None, scheduler);
        scheduler.RunUntilCompleted(started, watchdog.Token);
        Task<IOwnedReadOnlyList<(TreePath, byte[])>?> task = started.GetAwaiter().GetResult();
        try
        {
            scheduler.RunUntilCompleted(task, watchdog.Token);
        }
        finally
        {
            releaseNode.TrySetResult();
        }
        using IOwnedReadOnlyList<(TreePath, byte[])>? winner = task.GetAwaiter().GetResult();
        if (snapWins) scheduler.RunUntilCompleted(nodesDisposed.Task, watchdog.Token);
        // Transport disposal precedes the node-list result and its abandoned-result cleanup.
        scheduler.Drain(watchdog.Token);
        AssertRecoveredNode(winner);
        if (snapWins)
        {
            AssertProductionSpeedSample(_peerEth67_2);
        }
        else AssertSpeedSamples(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proofs.Count, Is.EqualTo(3));
            Assert.That(proofs.Select(proof => proof.DisposeCalls), Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(pendingNodes, Is.EqualTo(snapWins ? 2 : 0));
            Assert.That(nodeResponses.Count, Is.EqualTo(snapWins ? 2 : 0));
            Assert.That(nodeResponses.Select(response => response.DisposeCalls), Is.EqualTo(snapWins ? new[] { 1, 1 } : Array.Empty<int>()));
        }

        (TreePath, byte[])[][] returned = new (TreePath, byte[])[4][];
        for (int i = 0; i < returned.Length; i++) returned[i] = SafeArrayPool<(TreePath, byte[])>.Shared.Rent(1);
        try
        {
            Assert.That(returned.Count(buffer => seeded.Contains(buffer)), Is.EqualTo(3), "only the accepted node list stays owned");
            AssertRecoveredNode(winner);
        }
        finally
        {
            foreach ((TreePath, byte[])[] buffer in returned) SafeArrayPool<(TreePath, byte[])>.Shared.Return(buffer);
        }
    }

    private sealed class RecoveryTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly BlockingCollection<Task> _tasks = [];

        protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();
        protected override void QueueTask(Task task) => _tasks.Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        public void RunUntilCompleted(Task completion, CancellationToken cancellationToken)
        {
            while (!completion.IsCompleted)
                Execute(_tasks.Take(cancellationToken));
        }

        public void Drain(CancellationToken cancellationToken)
        {
            while (_tasks.TryTake(out Task? task))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Execute(task);
            }
        }

        private void Execute(Task task)
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                TryExecuteTask(task);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        public void Dispose() => _tasks.Dispose();
    }

    [Test]
    [NonParallelizable]
    public void Completed_losing_range_attempts_return_owned_buffers_without_extra_samples()
    {
        (TreePath, byte[])[][] seeded = new (TreePath, byte[])[3][];
        for (int i = 0; i < seeded.Length; i++) seeded[i] = SafeArrayPool<(TreePath, byte[])>.Shared.Rent(1);
        foreach ((TreePath, byte[])[] buffer in seeded) SafeArrayPool<(TreePath, byte[])>.Shared.Return(buffer);

        List<CountedBytes> proofs = [];
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            CountedBytes proof = new(_nodeRlp);
            proofs.Add(proof);
            return Task.FromResult(new AccountsAndProofs { Proofs = proof });
        });
        Task<IOwnedReadOnlyList<(TreePath, byte[])>?> recovery = Recover(_snapRecovery, _peerEth67);
        Assert.That(recovery.IsCompletedSuccessfully, Is.True);
        using IOwnedReadOnlyList<(TreePath, byte[])>? winner = recovery.GetAwaiter().GetResult();
        AssertRecoveredNode(winner);
        AssertSpeedSamples(1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(winner!.Count, Is.EqualTo(1));
            Assert.That(winner[0].Item2.Length, Is.EqualTo(_nodeRlp.Length));
            Assert.That(proofs.Count, Is.EqualTo(3));
            Assert.That(proofs.Select(proof => proof.DisposeCalls), Is.EqualTo(new[] { 1, 1, 1 }));
        }

        (TreePath, byte[])[][] returned = new (TreePath, byte[])[3][];
        for (int i = 0; i < returned.Length; i++) returned[i] = SafeArrayPool<(TreePath, byte[])>.Shared.Rent(1);
        try
        {
            Assert.That(returned.Count(buffer => seeded.Contains(buffer)), Is.EqualTo(2), "both losing node lists must return their buffers while the winner stays owned");
            AssertRecoveredNode(winner);
        }
        finally
        {
            foreach ((TreePath, byte[])[] buffer in returned) SafeArrayPool<(TreePath, byte[])>.Shared.Return(buffer);
        }
    }

    private sealed class CountedBytes(params byte[][] bytes) : IByteArrayList
    {
        public Action? OnDisposed { get; init; }
        public int DisposeCalls { get; private set; }
        public int Count => bytes.Length;
        public ReadOnlySpan<byte> this[int index] => bytes[index];
        public void Dispose()
        {
            DisposeCalls++;
            OnDisposed?.Invoke();
        }
    }

    [Test]
    public async Task Winning_peer_receives_exact_validated_rate([Values] bool code)
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int disposals = 0;
        ISnapSyncPeer delayedPeer = Substitute.For<ISnapSyncPeer>();
        _peerEth67.SyncPeer.TryGetSatelliteProtocol(Protocol.Snap, out Arg.Any<ISnapSyncPeer>()).Returns(call =>
        {
            call[1] = delayedPeer;
            return true;
        });
        byte[] unrequested = (byte[])_nodeRlp.Clone();
        unrequested[^1] ^= 1;
        async Task<IByteArrayList> DelayedResponse()
        {
            entered.TrySetResult();
            await release.Task;
            return new CountedBytes(_nodeRlp)
            {
                OnDisposed = () =>
                {
                    if (Interlocked.Increment(ref disposals) == 2) disposed.TrySetResult();
                }
            };
        }
        IByteArrayList WinningResponse()
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(7));
            return new CountedBytes(code ? [_nodeRlp] : [_nodeRlp, unrequested]);
        }
        delayedPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => DelayedResponse());
        delayedPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(async _ => new AccountsAndProofs { Proofs = await DelayedResponse() });
        _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(WinningResponse()));
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new AccountsAndProofs { Proofs = WinningResponse() }));
        SetupPeers([_peerEth67, _peerEth67_2]);
        try
        {
            if (code)
                Assert.That(await _codeRecovery.Recover(_hash.ValueHash256, cancellation.Token), Is.EqualTo(_nodeRlp));
            else
            {
                using IOwnedReadOnlyList<(TreePath, byte[])>? winner = await _snapRecovery.Recover(_rootHash, _storageHash, _path, _hash, _fullPath, cancellation.Token);
                AssertRecoveredNode(winner);
            }
            await entered.Task.WaitAsync(cancellation.Token);
            AssertSpeedSample(_peerEth67_2, _nodeRlp.Length / 7);
        }
        finally
        {
            release.TrySetResult();
        }
        await disposed.Task.WaitAsync(cancellation.Token);
        AssertSpeedSample(_peerEth67_2, _nodeRlp.Length / 7);
    }

    [Test]
    public async Task Validated_recovery_disposes_transport_and_returns_only_requested_bytes(
        [Values("code", "account", "storage")] string kind, [Values] bool valid)
    {
        List<CountedBytes> responses = [];
        List<StorageRange> storageRequests = [];
        byte[] unrequested = (byte[])_nodeRlp.Clone();
        unrequested[^1] ^= 1;
        CountedBytes Response()
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(5));
            CountedBytes response = kind == "code"
                ? new CountedBytes(valid ? _nodeRlp : unrequested)
                : valid ? new CountedBytes(_nodeRlp, unrequested) : new CountedBytes(unrequested);
            responses.Add(response);
            return response;
        }
        _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IByteArrayList>(Response()));
        _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new AccountsAndProofs { Proofs = Response() }));
        _snapSyncPeer.GetStorageRange(Arg.Any<StorageRange>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                storageRequests.Add(call.Arg<StorageRange>());
                return Task.FromResult(new SlotsAndProofs { Proofs = Response() });
            });
        if (kind == "code")
            Assert.That(await RecoverCode(_peerEth67_2), valid ? Is.EqualTo(_nodeRlp) : Is.Null);
        else
        {
            if (kind == "storage") _storageHash = TestItem.KeccakB;
            using IOwnedReadOnlyList<(TreePath, byte[])>? result = await Recover(_snapRecovery, _peerEth67_2);
            if (valid)
            {
                AssertRecoveredNode(result);
                Assert.That(result!.Sum(node => node.Item2.Length), Is.EqualTo(_nodeRlp.Length));
            }
            else Assert.That(result, Is.Null);
        }
        if (valid) AssertSpeedSample(_peerEth67_2, _nodeRlp.Length / 5);
        else AssertSpeedSamples(0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(responses.Count, Is.EqualTo(3));
            Assert.That(responses.Select(response => response.DisposeCalls), Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(storageRequests.Count, Is.EqualTo(kind == "storage" ? 3 : 0));
        }
        foreach (StorageRange request in storageRequests)
            Assert.Throws<ObjectDisposedException>(() => { _ = request.Accounts.AsSpan().Length; });
    }

    [Test]
    public async Task Public_two_argument_constructors_recover_without_stats([Values] bool code)
    {
        SetupPeers([_peerEth67]);
        if (code)
        {
            CodeRecovery recovery = new(_syncPeerPool, LimboLogs.Instance);
            Assert.That(await recovery.Recover(_hash.ValueHash256), Is.EqualTo(_nodeRlp));
        }
        else
        {
            SnapRangeRecovery recovery = new(_syncPeerPool, LimboLogs.Instance);
            using IOwnedReadOnlyList<(TreePath, byte[])>? response = await recovery.Recover(_rootHash, _storageHash, _path, _hash, _fullPath, CancellationToken.None);
            AssertRecoveredNode(response);
        }
        AssertSpeedSamples(0);
    }

    [Test]
    public async Task Cancelled_recovery_ignores_valid_late_data_and_records_no_sample([Values] bool code)
    {
        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (code)
            _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return (IByteArrayList)new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _nodeRlp });
            });
        else
            _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return new AccountsAndProofs { Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _nodeRlp }) };
            });
        SetupPeers([_peerEth67]);
        Task<byte[]?>? codeTask = code ? _codeRecovery.Recover(_hash.ValueHash256, cancellation.Token) : null;
        Task<IOwnedReadOnlyList<(TreePath, byte[])>?>? pathTask = code ? null
            : _snapRecovery.Recover(_rootHash, _storageHash, _path, _hash, _fullPath, cancellation.Token);
        await entered.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        release.SetResult();
        if (code) Assert.That(await codeTask!.WaitAsync(TimeSpan.FromSeconds(10)), Is.Null);
        else Assert.That(await pathTask!.WaitAsync(TimeSpan.FromSeconds(10)), Is.Null);
        AssertSpeedSamples(0);
    }

    [Test]
    public async Task Code_recovery_preserves_a_verified_winner_before_cancellation([Values] bool cancelBeforeResult)
    {
        using CancellationTokenSource cancellation = new();
        int allocations = 0;
        _syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (++allocations == 2) cancellation.Cancel();
                SyncPeerAllocation allocation = new(call.Arg<AllocationContexts>());
                allocation.AllocatePeer(_peerEth67);
                return allocation;
            });
        _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (cancelBeforeResult) cancellation.Cancel();
                return Task.FromResult<IByteArrayList>(new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { _nodeRlp }));
            });

        byte[]? result = await _codeRecovery.Recover(_hash.ValueHash256, cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancellation.IsCancellationRequested, Is.True);
            Assert.That(allocations, Is.EqualTo(3));
            Assert.That(result, cancelBeforeResult ? Is.Null : Is.EqualTo(_nodeRlp));
        }
        AssertSpeedSamples(cancelBeforeResult ? 0 : 1);
    }

    [Test]
    public async Task Hash_validated_empty_code_records_no_sample()
    {
        _returnedRlp = [];
        _hash = Keccak.Compute(_returnedRlp);
        Assert.That(await RecoverCode(_peerEth67), Is.Empty);
        AssertSpeedSamples(0);
    }

    [Test]
    public async Task Transport_failure_records_no_recovery_sample([Values] bool code, [Values] bool timeout)
    {
        Exception failure = timeout ? new TimeoutException() : new InvalidOperationException("transport unavailable");
        if (code)
        {
            _snapSyncPeer.GetByteCodes(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<IByteArrayList>(failure));
            Assert.That(await RecoverCode(_peerEth67), Is.Null);
        }
        else
        {
            _snapSyncPeer.GetAccountRange(Arg.Any<AccountRange>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<AccountsAndProofs>(failure));
            Assert.That(await Recover(_snapRecovery, _peerEth67), Is.Null);
        }
        AssertSpeedSamples(0);
    }

    private void AssertSpeedSamples(int count)
    {
        if (count > 0)
            _stats.Received(count).ReportTransferSpeedEvent(_peerEth67.SyncPeer.Node, TransferSpeedType.SnapRanges, _nodeRlp.Length);
        Assert.That(_stats.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(INodeStatsManager.ReportTransferSpeedEvent)), Is.EqualTo(count));
    }

    private void AssertSpeedSample(PeerInfo peer, long rate)
    {
        _stats.Received(1).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, rate);
        Assert.That(_stats.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(INodeStatsManager.ReportTransferSpeedEvent)), Is.EqualTo(1));
    }

    private void AssertProductionSpeedSample(PeerInfo peer)
    {
        _stats.Received(1).ReportTransferSpeedEvent(peer.SyncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
        Assert.That(_stats.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(INodeStatsManager.ReportTransferSpeedEvent)), Is.EqualTo(1));
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
