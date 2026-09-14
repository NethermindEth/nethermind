// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Evm.State;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Test;

public class MigrationBalFollowerTests
{
    private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");

    [Test]
    public async Task Each_authenticated_bal_matches_independent_root_after_restart([Values("a", "b")] string branch)
    {
        using Harness harness = new();
        await harness.Publish();
        harness.Canonical(branch);
        int count = branch == "a" ? 5 : 6;
        for (int number = 1; number <= count; number++)
        {
            string name = number == 1 ? "a1" : branch + number;
            Assert.That(await harness.Follower.Follow(harness.Blocks[name].Header, default), Is.True, harness.Follower.Error);
            Assert.That(harness.Follower.Cursor!.Hash, Is.EqualTo(harness.Blocks[name].Hash));
            AssertState(harness, name);
            harness.Reopen();
            AssertState(harness, name);
        }
    }

    [Test]
    public async Task Reorg_replays_alternate_branch_from_the_retained_common_ancestor()
    {
        using Harness harness = new();
        await harness.Publish();
        harness.Canonical("a");
        Assert.That(await harness.Follower.Follow(harness.Blocks["a5"].Header, default), Is.True);
        AssertState(harness, "a5");
        harness.Canonical("b");

        Assert.That(await harness.Follower.Follow(harness.Blocks["b6"].Header, default), Is.True, harness.Follower.Error);

        AssertState(harness, "b6");
        AssertState(harness, "a5");
    }

    [Test]
    public async Task Reorg_ahead_of_cursor_does_not_rewind_retained_state()
    {
        using Harness harness = new();
        await harness.Publish();
        Assert.That(await harness.Follower.Follow(harness.Blocks["a1"].Header, default), Is.True);
        harness.Canonical("b");

        Assert.That(await harness.Follower.Follow(harness.Blocks["b3"].Header, default), Is.True, harness.Follower.Error);

        AssertState(harness, "b3");
        AssertState(harness, "a1");
    }

    [Test]
    public async Task Replay_requires_the_retained_parent_and_commits_the_child_once()
    {
        using Harness harness = new();
        await harness.Publish();
        Assert.Throws<InvalidOperationException>(() => harness.Replay.Apply(harness.Blocks["a1"].Header, harness.Blocks["a2"].Header, harness.Bal("a2")));
        Assert.That(harness.Manager.HasStateForBlock(new StateId(harness.Blocks["a2"].Header)), Is.False);
        for (int attempt = 0; attempt < 2; attempt++) harness.Replay.Apply(harness.Blocks["anchor"].Header, harness.Blocks["a1"].Header, harness.Bal("a1"));
        AssertState(harness, "a1");
    }

    [Test]
    public async Task Missing_or_corrupt_bal_does_not_skip_gap_and_can_resume([Values] bool corrupt)
    {
        using Harness harness = new();
        await harness.Publish();
        harness.Canonical("a");
        Block missing = harness.Blocks["a2"];
        if (corrupt) harness.Store.Insert(missing.Number, missing.Hash!, Bytes.FromHexString("c0"));
        else harness.Store.Delete(missing.Number, missing.Hash!);

        Assert.That(await harness.Follower.Follow(harness.Blocks["a3"].Header, default), Is.False);

        AssertState(harness, "a1");
        Assert.That(harness.Manager.HasStateForBlock(new StateId(missing.Header)), Is.False);
        Assert.That(harness.Follower.Error, Is.Not.Null.And.Not.Empty);
        harness.Reopen();
        harness.RestoreBal("a2");
        Assert.That(await harness.Follower.Follow(harness.Blocks["a3"].Header, default), Is.True, harness.Follower.Error);
        AssertState(harness, "a3");
    }

    [Test]
    public async Task Cancellation_or_reorg_during_acquisition_discards_private_replay([Values] bool reorg)
    {
        using Harness harness = new();
        using CancellationTokenSource cancellation = new();
        await harness.Publish();
        harness.Canonical("a");
        harness.OnRead = hash =>
        {
            if (hash != harness.Blocks["a2"].Hash) return;
            harness.OnRead = null;
            if (reorg) harness.Canonical("b");
            else cancellation.Cancel();
        };

        if (reorg) Assert.That(await harness.Follower.Follow(harness.Blocks["a3"].Header, cancellation.Token), Is.False);
        else Assert.ThrowsAsync<OperationCanceledException>(() => harness.Follower.Follow(harness.Blocks["a3"].Header, cancellation.Token));

        AssertState(harness, "a1");
        Assert.That(harness.Manager.HasStateForBlock(new StateId(harness.Blocks["a2"].Header)), Is.False);
        harness.Reopen();
        string target = reorg ? "b3" : "a3";
        Assert.That(await harness.Follower.Follow(harness.Blocks[target].Header, default), Is.True, harness.Follower.Error);
        AssertState(harness, target);
    }

    [Test]
    public async Task Empty_bal_child_retains_actual_pbt_root_not_mpt_header_root()
    {
        using Harness harness = new();
        await harness.Publish();
        BlockHeader parent = harness.Blocks["anchor"].Header;
        BlockHeader child = Build.A.BlockHeader.WithParent(parent).WithStateRoot(parent.StateRoot!).WithTimestamp(12).TestObject;
        byte[] bal = Bytes.FromHexString("c0");
        child.BlockAccessListHash = Keccak.Compute(bal);
        child.Hash = child.CalculateHash();
        harness.AddCanonical(new Block(child), bal);

        Assert.That(await harness.Follower.Follow(child, default), Is.True, harness.Follower.Error);

        Hash256 anchorRoot = new(harness.Metadata["anchor"].GetProperty("pbtRoot").GetString()!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.TreeRoot(child), Is.EqualTo(anchorRoot));
            Assert.That(harness.TreeRoot(child), Is.Not.EqualTo(child.StateRoot));
            Assert.That(harness.Follower.Cursor!.Hash, Is.EqualTo(child.Hash));
            Assert.That(harness.Follower.Cursor.TreeRoot, Is.EqualTo(anchorRoot));
        }
    }

    private static void AssertState(Harness harness, string name)
    {
        BlockHeader header = harness.Blocks[name].Header;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Manager.HasStateForBlock(new StateId(header)), Is.True, name);
            Assert.That(harness.TreeRoot(header), Is.EqualTo(new Hash256(harness.Metadata[name].GetProperty("pbtRoot").GetString()!)), name);
        }
    }

    internal sealed class Harness : IDisposable
    {
        private readonly IContainer _container;
        private readonly TempPath _scratch = TempPath.GetTempDirectory();
        private readonly SnapshotableMemColumnsDb<PbtColumns> _target = new("pbt");
        private readonly MemDb _balDb = new();
        private readonly IBlockTree _blockTree;
        private readonly BalFetcher _fetcher;
        private readonly IBlockAccessListStore _observedStore;
        private PbtTestContext _pbt = null!;
        public readonly Dictionary<string, Block> Blocks = [];
        public readonly Dictionary<string, JsonElement> Metadata = [];
        public readonly BlockAccessListStore Store;
        public Action<Hash256>? OnRead;
        public PbtBalFollower Follower { get; private set; } = null!;
        public PbtBalReplay Replay { get; private set; } = null!;
        public IPbtDbManager Manager => _pbt.Manager;
        public IPbtPersistence Persistence => _pbt.Persistence;

        public Harness()
        {
            using Stream genesis = typeof(MigrationBalFollowerTests).Assembly.GetManifestResourceStream("Nethermind.State.Pbt.Test.Fixtures.Eip8347.genesis.json")!;
            ChainSpec chain = new GethGenesisLoader(new EthereumJsonSerializer()).Load(genesis);
            _container = new ContainerBuilder().AddModule(new TestNethermindModule(new Nethermind.Config.ConfigProvider(new FlatDbConfig { Enabled = false }), chain, useTestSpecProvider: false))
                .AddSingleton<IPbtConfig>(new PbtConfig())
                .Build();
            _blockTree = _container.Resolve<IBlockTree>();
            Store = new(_balDb);
            ISyncPeerPool peers = Substitute.For<ISyncPeerPool>();
            peers.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => new SyncPeerAllocation(AllocationContexts.State));
            IBlockAccessListStore observedStore = _observedStore = Substitute.For<IBlockAccessListStore>();
            observedStore.GetRlp(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(call =>
            {
                OnRead?.Invoke(call.Arg<Hash256>());
                return Store.GetRlp(call.Arg<ulong>(), call.Arg<Hash256>());
            });
            observedStore.When(store => store.Delete(Arg.Any<ulong>(), Arg.Any<Hash256>()))
                .Do(call => Store.Delete(call.Arg<ulong>(), call.Arg<Hash256>()));
            _fetcher = new(peers, _blockTree, Store, new SyncConfig { SyncDispatcherAllocateTimeoutMs = 1 }, LimboLogs.Instance);
            using JsonDocument blocks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "blocks.json")));
            foreach (JsonElement metadata in blocks.RootElement.EnumerateArray())
            {
                string name = metadata.GetProperty("name").GetString()!;
                BlockHeader header = Rlp.Decode<BlockHeader>(new Rlp(Bytes.FromHexString(metadata.GetProperty("headerRlp").GetString()!)))!;
                Blocks.Add(name, new Block(header));
                Metadata.Add(name, metadata.Clone());
                _blockTree.SuggestBlock(Blocks[name]);
                if (name != "anchor") RestoreBal(name);
            }
            Canonical("a");
            Open();
        }

        public void Canonical(string branch)
        {
            string[] names = branch == "a" ? ["anchor", "a1", "a2", "a3", "a4", "a5"] : ["anchor", "a1", "b2", "b3", "b4", "b5", "b6"];
            foreach (string name in names)
            {
                Block block = Blocks[name];
                _blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: [block]);
            }
        }

        public ISpecProvider SpecProvider => _container.Resolve<ISpecProvider>();
        public IContainer Container => _container;
        public IBlockTree BlockTree => _blockTree;

        public Hash256 TreeRoot(BlockHeader header)
        {
            using PbtReadOnlySnapshotBundle bundle = Manager.GatherReadOnlyBundle(new StateId(header));
            return bundle.TreeRoot.ToHash256();
        }

        public void AddCanonical(Block block, byte[] bal)
        {
            _blockTree.SuggestBlock(block);
            _blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: [block]);
            Store.Insert(block.Number, block.Hash!, bal);
        }

        public void RestoreBal(string name) => Store.Insert(Blocks[name].Number, Blocks[name].Hash!, Bytes.FromHexString(Metadata[name].GetProperty("balRlp").GetString()!));

        public ReadOnlyBlockAccessList Bal(string name) =>
            Rlp.Decode<ReadOnlyBlockAccessList>(Bytes.FromHexString(Metadata[name].GetProperty("balRlp").GetString()!))!;

        /// <summary>Persists everything and reopens the native PBT stack over the same database, as a restart would.</summary>
        public void Reopen()
        {
            Manager.FlushCache(CancellationToken.None);
            Close();
            Open();
        }

        private void Open()
        {
            _pbt = new PbtTestContext(_target);
            Replay = new PbtBalReplay(_container, _pbt.Manager, _pbt.ResourcePool, _pbt.CodeDb, SpecProvider, LimboLogs.Instance);
            Follower = CreateFollower(_blockTree, _fetcher, _observedStore);
        }

        private void Close() => _pbt.DisposeAsync().AsTask().GetAwaiter().GetResult();

        public PbtBalFollower CreateFollower(IBlockTree blockTree, BalFetcher fetcher, IBlockAccessListStore store) =>
            new(blockTree, fetcher, store, _pbt.Manager, Replay, SpecProvider, LimboLogs.Instance) { MigrationRetryDelay = TimeSpan.Zero };

        public async Task Publish()
        {
            BlockHeader header = Blocks["anchor"].Header;
            PbtImageAnchor anchor = new("1", header.Hash!, header, true, 48, 24576);
            PbtArtifactIdentity identity = new("1", header.Hash!.ToString(), header.Hash.ToString(), header.Number,
                header.StateRoot!.ToString(), "eip-8347", "test", "fixture-state");
            Directory.CreateDirectory(_scratch.Path);
            using FileStream snapshot = File.OpenRead(Path.Combine(Fixtures, "canonical", "anchor", "snapshot.pbt"));
            using FileStream preimages = File.OpenRead(Path.Combine(Fixtures, "canonical", "anchor", "preimages.bin"));
            await new PbtAnchorPublication(new PbtRocksDbPersistence(_target, new PbtConfig()), _target, _pbt.Persistence, _pbt.Manager, _pbt.Coordinator, LimboLogs.Instance)
                .Publish(snapshot, preimages, identity, anchor, _scratch.Path, () => true, default);
        }

        public void Dispose()
        {
            Close();
            _container.Dispose();
            _target.Dispose();
            _balDb.Dispose();
            _scratch.Dispose();
        }
    }
    public class AcquisitionTests
    {
        private const int BlockCount = 300;
        private readonly Dictionary<ValueHash256, byte[]> _balByHash = [];
        private int _largestRequest;
        private int _responseLimit;
        private readonly List<SyncPeerAllocation> _allocations = [];
        private Action? _onFetch;
        private IBlockTree _blockTree = null!;
        private MemDb _balDb = null!;
        private BlockAccessListStore _balStore = null!;
        private ISyncPeerPool _pool = null!;
        private PbtBalFollower _follower = null!;
        private Harness _harness = null!;

        [SetUp]
        public void SetUp()
        {
            _balByHash.Clear();
            _largestRequest = 0;
            _responseLimit = int.MaxValue;
            _allocations.Clear();
            _onFetch = null;
            _harness = new();
            _blockTree = Substitute.For<IBlockTree>();
            _balDb = new();
            _balStore = new(_balDb);
            _pool = Substitute.For<ISyncPeerPool>();
            BalFetcher fetcher = new(_pool, _blockTree, _balStore, new SyncConfig { SyncDispatcherAllocateTimeoutMs = 5 }, LimboLogs.Instance);
            _follower = _harness.CreateFollower(_blockTree, fetcher, _balStore);
        }

        [TearDown]
        public async Task TearDown()
        {
            _harness.Dispose();
            _balDb.Dispose();
            await _pool.DisposeAsync();
        }

        [Test]
        public async Task Migration_acquires_owned_input_despite_store_pruning([Values] bool stored, [Values] bool snap2)
        {
            BlockHeader from = Block(10, null);
            byte[] valid = Bytes.FromHexString("c0");
            BlockHeader to = MigrationBlock(from, valid);
            if (stored) _balStore.Insert(to.Number, to.Hash!, valid);
            AllocatePeer(snap2 ? Snap2Peer() : Eth71Peer());
            byte[]? retained = null;
            bool result = await _follower.AcquireRange(from, to, async (header, bytes, token) =>
            {
                _balStore.Delete(header.Number, header.Hash!);
                await Task.Yield();
                retained = bytes.ToArray();
            }, default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(retained, Is.EqualTo(valid));
                Assert.That(_largestRequest, Is.EqualTo(stored ? 0 : 1));
            }
        }

        [TestCase("dead", true, true)]
        [TestCase("c0c0", false, true)]
        [TestCase("01", false, true)]
        [TestCase("c0c0", false, false)]
        [TestCase("01", false, false)]
        public async Task Migration_authenticates_existing_bytes_and_structure(string storedHex, bool repairable, bool locallyStored)
        {
            BlockHeader from = Block(10, null);
            byte[] stored = Bytes.FromHexString(storedHex);
            BlockHeader to = MigrationBlock(from, repairable ? Bytes.FromHexString("c0") : stored);
            if (locallyStored) _balStore.Insert(to.Number, to.Hash!, stored);
            AllocatePeer(Snap2Peer());
            int consumed = 0;

            bool result = await _follower.AcquireRange(from, to, (header, bytes, token) =>
            {
                consumed++;
                return Task.CompletedTask;
            }, default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(repairable));
                Assert.That(consumed, Is.EqualTo(repairable ? 1 : 0));
                Assert.That(_balStore.Exists(to.Number, to.Hash!), Is.EqualTo(repairable));
                Assert.That(_allocations, Has.Count.EqualTo(repairable ? 1 : 50));
            }
        }

        [Test]
        public async Task Migration_rejects_chain_change_during_fetch([Values] bool restoreOriginal)
        {
            BlockHeader from = Block(10, null);
            BlockHeader to = MigrationBlock(from, Bytes.FromHexString("c0"));
            BlockHeader replacement = Build.A.BlockHeader.WithNumber(to.Number).WithExtraData(Bytes.FromHexString("01")).TestObject;
            _onFetch = () =>
            {
                _blockTree.FindHeader(to.Number).Returns(replacement);
                _blockTree.OnUpdateMainChain += Raise.EventWith(new OnUpdateMainChainArgs([replacement], true));
                if (restoreOriginal) _blockTree.FindHeader(to.Number).Returns(to);
            };
            AllocatePeer(Snap2Peer());
            int consumed = 0;
            bool result = await _follower.AcquireRange(from, to, (header, bytes, token) =>
            {
                consumed++;
                return Task.CompletedTask;
            }, default);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.False);
                Assert.That(consumed, Is.Zero);
                Assert.That(_allocations, Has.Count.EqualTo(1));
                Assert.That(_allocations[0].Current, Is.Null);
            }
        }

        [Test]
        public async Task Migration_does_not_report_partial_range_ready([Values] bool cancel)
        {
            BlockHeader from = Block(10, null);
            BlockHeader first = MigrationBlock(from, Bytes.FromHexString("c0"));
            BlockHeader to = MigrationBlock(first, Bytes.FromHexString("c0"));
            _balByHash.Remove(to.Hash!.ValueHash256);
            AllocatePeer(Snap2Peer());
            using CancellationTokenSource cancellation = new();
            int consumed = 0;
            Task<bool> task = _follower.AcquireRange(from, to, (header, bytes, token) =>
            {
                consumed++;
                if (cancel) cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token);

            if (cancel) Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
            else Assert.That(await task, Is.False);
            Assert.That(consumed, Is.EqualTo(1));
        }

        [Test]
        public async Task Migration_rejects_disconnected_ancestry_and_accepts_empty_range()
        {
            BlockHeader from = Block(10, null);
            BlockHeader to = Block(11, Bytes.FromHexString("c0"));
            Assert.That(await _follower.AcquireRange(from, to, (_, _, _) => Task.CompletedTask, default), Is.False);
            Assert.That(await _follower.AcquireRange(from, from, (_, _, _) => Task.CompletedTask, default), Is.True);
            await _pool.DidNotReceive().Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }

        [Test]
        public async Task Migration_rechecks_after_consumer_but_allows_tip_extension([Values] bool reorg)
        {
            BlockHeader from = Block(10, null);
            BlockHeader to = MigrationBlock(from, Bytes.FromHexString("c0"));
            AllocatePeer(Snap2Peer());
            bool result = await _follower.AcquireRange(from, to, (header, bytes, token) =>
            {
                BlockHeader changed = Build.A.BlockHeader.WithNumber(reorg ? header.Number : header.Number + 1).TestObject;
                _blockTree.OnUpdateMainChain += Raise.EventWith(new OnUpdateMainChainArgs([changed], true));
                return Task.CompletedTask;
            }, default);
            Assert.That(result, Is.EqualTo(!reorg));
        }

        [Test]
        public async Task Migration_streams_multiple_windows_in_hash_order()
        {
            BlockHeader from = Block(10, null);
            BlockHeader to = from;
            for (int index = 0; index < BlockCount; index++) to = MigrationBlock(to, Bytes.FromHexString("c0"));
            AllocatePeer(Snap2Peer());
            Hash256 parentHash = from.Hash!;
            int consumed = 0;
            bool result = await _follower.AcquireRange(from, to, (header, bytes, token) =>
            {
                Assert.That(header.ParentHash, Is.EqualTo(parentHash));
                parentHash = header.Hash!;
                consumed++;
                return Task.CompletedTask;
            }, default);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(parentHash, Is.EqualTo(to.Hash));
                Assert.That(consumed, Is.EqualTo(BlockCount));
                Assert.That(_largestRequest, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task Migration_disposes_borrowed_memory_after_awaited_callback([Values] bool cancel)
        {
            BlockHeader from = Block(10, null);
            BlockHeader to = MigrationBlock(from, Bytes.FromHexString("c0"));
            TrackingMemory owner = new(Bytes.FromHexString("c0"));
            IBlockAccessListStore store = Substitute.For<IBlockAccessListStore>();
            store.GetRlp(to.Number, to.Hash!).Returns(owner);
            BalFetcher fetcher = new(_pool, _blockTree, store, new SyncConfig(), LimboLogs.Instance);
            PbtBalFollower follower = _harness.CreateFollower(_blockTree, fetcher, store);
            Task<bool> task = follower.AcquireRange(from, to, async (_, bytes, _) =>
            {
                await Task.Yield();
                Assert.That(owner.Disposed, Is.False);
                Assert.That(bytes.ToArray(), Is.EqualTo(Bytes.FromHexString("c0")));
                if (cancel) throw new OperationCanceledException();
            }, default);
            if (cancel) Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
            else Assert.That(await task, Is.True);
            Assert.That(owner.Disposed, Is.True);
        }

        private sealed class TrackingMemory(byte[] bytes) : MemoryManager<byte>
        {
            public bool Disposed { get; private set; }
            public override Span<byte> GetSpan() => bytes;
            public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
            public override void Unpin() { }
            protected override void Dispose(bool disposing) => Disposed = true;
        }

        private BlockHeader MigrationBlock(BlockHeader parent, byte[] bal)
        {
            BlockHeader header = Build.A.BlockHeader.WithParent(parent).WithNumber(parent.Number + 1)
                .WithBlockAccessListHash(Keccak.Compute(bal)).TestObject;
            _blockTree.FindHeader(header.Number).Returns(header);
            _balByHash[header.Hash!.ValueHash256] = bal;
            return header;
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
                .Returns(ci =>
                {
                    AllocationContexts contexts = ci.Arg<AllocationContexts>();
                    SyncPeerAllocation allocation = new(contexts);
                    _allocations.Add(allocation);
                    allocation.AllocatePeer(peer);
                    return allocation;
                });

        private PeerInfo Snap2Peer()
        {
            ISnapSyncPeer snap = Substitute.For<ISnapSyncPeer>();
            snap.SnapProtocolVersion.Returns(SnapVersions.Snap2);
            snap.GetBlockAccessLists(Arg.Any<IReadOnlyList<ValueHash256>>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    _onFetch?.Invoke();
                    IReadOnlyList<ValueHash256> requested = ci.Arg<IReadOnlyList<ValueHash256>>();
                    _largestRequest = Math.Max(_largestRequest, requested.Count);
                    int served = Math.Min(requested.Count, _responseLimit);
                    ArrayPoolList<byte[]> response = new(served);
                    for (int i = 0; i < served; i++) response.Add(_balByHash.GetValueOrDefault(requested[i], []));
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
                    _largestRequest = Math.Max(_largestRequest, requested.Count);
                    ArrayPoolList<byte[]?> response = new(requested.Count);
                    foreach (Hash256 hash in requested)
                        response.Add(_balByHash.TryGetValue(hash.ValueHash256, out byte[]? rlp) ? rlp : null);
                    return Task.FromResult<IOwnedReadOnlyList<byte[]?>>(response);
                });
            return new PeerInfo(syncPeer);
        }
    }
}
