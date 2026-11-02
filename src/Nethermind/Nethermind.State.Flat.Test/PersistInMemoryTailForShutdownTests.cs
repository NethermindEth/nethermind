// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Visitors;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PersistInMemoryTailForShutdownTests
{
    private static readonly StateId Block0 = new(0, Keccak.EmptyTreeHash);

    private static StateId State(ulong number, byte fork = 0) => new(number, Keccak.Compute($"{number}-{fork}"));

    private static FlatDbConfig Config(bool longFinality = true) => new()
    {
        CompactSize = 16,
        CompactionOffset = 0,
        MinReorgDepth = 64,
        MaxInMemoryBaseSnapshotCount = 160,
        EnableLongFinality = longFinality,
    };

    [Test]
    public void Converts_the_committed_chain_and_it_survives_a_restart()
    {
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        StateId head = State(3);
        using (Session session = new(Config(), dir.Path, catalog))
        {
            session.AddChain(Block0, 3);
            // A fork off the committed chain stays behind: only the head's ancestry is needed to resume from it.
            session.Add(State(1), State(2, fork: 1));
            session.Repository.SetLastCommittedStateId(head);

            int converted = session.Manager.PersistInMemoryTailForShutdown(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(converted, Is.EqualTo(3));
                Assert.That(session.Repository.HasBasePersistedSnapshot(State(1)), Is.True);
                Assert.That(session.Repository.HasBasePersistedSnapshot(head), Is.True);
                Assert.That(session.Repository.HasBasePersistedSnapshot(State(2, fork: 1)), Is.False, "fork");
                Assert.That(session.Barrier.ReceivedCalls(), Is.Not.Empty, "deferred block data is made durable before the state");
            }
        }

        using Session restarted = new(Config(), dir.Path, catalog);
        using AssembledSnapshotResult assembled = restarted.Repository.AssembleSnapshots(head, Block0, estimatedSize: 4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restarted.Repository.HasState(head), Is.True, "the head's state is available after the restart");
            Assert.That(assembled.SnapshotCount, Is.EqualTo(3), "and reachable from the persisted state");
            Assert.That(restarted.Repository.HasState(State(2, fork: 1)), Is.False);
        }
    }

    [Test]
    public void Converts_only_what_is_missing_above_the_persisted_tier()
    {
        using TempPath dir = TempPath.GetTempDirectory();
        using Session session = new(Config(), dir.Path, new MemDb());
        session.AddChain(Block0, 4);
        // As after a normal conversion: the oldest blocks of the tail are already in the persisted tier.
        using (Snapshot? first = session.Lease(State(1))) session.Loader.ConvertAndRegister(first!);
        using (Snapshot? second = session.Lease(State(2))) session.Loader.ConvertAndRegister(second!);
        session.Repository.SetLastCommittedStateId(State(4));

        int converted = session.Manager.PersistInMemoryTailForShutdown(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(converted, Is.EqualTo(2));
            Assert.That(session.Repository.HasBasePersistedSnapshot(State(4)), Is.True);
        }
    }

    [Test]
    public void Nothing_is_converted_when_cancelled_without_long_finality_or_across_a_gap(
        [Values("cancelled", "no long finality", "gap", "no committed head")] string reason)
    {
        using TempPath dir = TempPath.GetTempDirectory();
        using Session session = new(Config(longFinality: reason != "no long finality"), dir.Path, new MemDb());
        session.AddChain(Block0, 3);
        if (reason == "gap") session.Add(State(5), State(6));
        StateId head = reason == "gap" ? State(6) : State(3);
        if (reason != "no committed head") session.Repository.SetLastCommittedStateId(head);
        using CancellationTokenSource cancelled = new();
        if (reason == "cancelled") cancelled.Cancel();

        int converted = session.Manager.PersistInMemoryTailForShutdown(cancelled.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(converted, Is.Zero);
            Assert.That(session.Repository.HasBasePersistedSnapshot(State(1)), Is.False);
            Assert.That(session.Barrier.ReceivedCalls(), Is.Empty, "nothing to persist, nothing to flush");
        }
    }

    // The whole shutdown path: blocks committed through the manager, a clean dispose, and a restart on the same disk.
    [Test]
    public async Task A_clean_shutdown_keeps_the_head_state_for_the_next_start([Values] bool longFinality)
    {
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        FlatDbConfig config = Config(longFinality);
        StateId head = State(3);
        using (FlatTestContainer node = new(config, baseDbPath: dir.Path, catalogDb: catalog))
        {
            FlatDbManager manager = (FlatDbManager)node.Resolve<IFlatDbManager>();
            StateId parent = StateId.PreGenesis;
            for (ulong n = 0; n <= head.BlockNumber; n++)
            {
                StateId to = n == 0 ? Block0 : State(n);
                Snapshot snapshot = node.ResourcePool.CreateSnapshot(parent, to, ResourcePool.Usage.MainBlockProcessing);
                snapshot.Content.Accounts[TestItem.AddressA] = new Account(n, 100);
                manager.AddSnapshot(snapshot, node.ResourcePool.GetCachedResource(ResourcePool.Usage.MainBlockProcessing));
                parent = to;
            }

            Assert.That(manager.HasStateForBlock(head), Is.True);
            await manager.DisposeAsync();
        }

        // The restarted container loads the persisted tier itself, as the manager would on construction.
        using FlatTestContainer restarted = new(config, baseDbPath: dir.Path, catalogDb: catalog);
        Assert.That(restarted.Repository.HasState(head), Is.EqualTo(longFinality),
            "with long finality the in-memory tail is kept; without it there is no persisted-snapshot tier to keep it in");
    }

    [Test]
    public async Task Rocksdb_restart_resumes_the_durable_prefix_and_can_reorg([Values] bool interruptConversion)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        CatalogFault fault = new();
        Block[] blocks = new Block[4];
        await using (IContainer node = OpenDiskNode(directory.Path, fault))
        {
            Assert.That(node.ResolveKeyed<IDb>(DbNames.PersistedSnapshotCatalog), Is.InstanceOf<DbOnTheRocks>());
            MainProcessingContext processing = node.Resolve<MainProcessingContext>();
            IWorldState state = processing.WorldState;
            using (state.BeginScope(IWorldState.PreGenesis))
            {
                state.CreateAccount(TestItem.AddressA, 1_000_000);
                state.Commit(Berlin.Instance);
                state.RecalculateStateRoot();
                blocks[0] = Build.A.Block.Genesis.WithStateRoot(state.StateRoot).TestObject;
                state.CommitTree(0);
            }
            IBlockTree tree = node.Resolve<IBlockTree>();
            tree.SuggestBlock(blocks[0]);
            Assert.That(tree.TryUpdateMainChain(blocks[0].Header, true, preloadedBlocks: [blocks[0]]), Is.True);
            node.Resolve<IWorldStateManager>().FlushCache(CancellationToken.None);

            for (int i = 1; i < blocks.Length; i++)
                blocks[i] = ExecuteTransfer(node, blocks[i - 1], TestItem.AddressB, 10);

            Assert.That(node.Resolve<IPersistenceManager>().GetCurrentPersistedStateId().BlockNumber, Is.Zero);
            fault.Enabled = interruptConversion;
        }

        Assert.That(fault.Triggered, Is.EqualTo(interruptConversion));
        if (interruptConversion) Assert.That(fault.Attempts, Is.EqualTo(2));
        Block reorgParent;
        Block reorgHead;
        await using (IContainer restarted = OpenDiskNode(directory.Path))
        {
            IBlockTree tree = restarted.Resolve<IBlockTree>();
            IStateReader reader = restarted.Resolve<IStateReader>();
            ulong kept = interruptConversion ? 1UL : 3UL;
            Assert.That(tree.Head!.Number, Is.Zero, "RocksDB's base must not move to the unfinalized head");
            Assert.That(KeptStateResumer.Resume(tree, reader, LimboLogs.Instance), Is.EqualTo(kept));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tree.Head!.Hash, Is.EqualTo(blocks[kept].Hash));
                Assert.That(reader.GetBalance(blocks[kept].Header, TestItem.AddressB), Is.EqualTo((UInt256)(kept * 10)));
                Assert.That(reader.HasStateForBlock(blocks[2].Header), Is.EqualTo(!interruptConversion));
                Assert.That(reader.HasStateForBlock(blocks[3].Header), Is.EqualTo(!interruptConversion));
                Assert.That(restarted.Resolve<IPersistenceManager>().GetCurrentPersistedStateId().BlockNumber, Is.Zero);
            }

            using (Assert.EnterMultipleScope())
            {
                for (int i = 2; i < blocks.Length; i++)
                {
                    Assert.That(tree.FindBlock(blocks[i].Hash!, BlockTreeLookupOptions.None)?.Hash, Is.EqualTo(blocks[i].Hash));
                    Assert.That(tree.WasProcessed(blocks[i].Number, blocks[i].Hash!), Is.True);
                }
            }

            for (ulong i = kept + 1; i < (ulong)blocks.Length; i++)
            {
                Block original = tree.FindBlock(blocks[i].Hash!, BlockTreeLookupOptions.None)!;
                Block replayed = Execute(restarted, blocks[i - 1], original);
                Assert.That(replayed.Hash, Is.EqualTo(blocks[i].Hash), "replay must reproduce the original state and block");
            }

            reorgParent = ExecuteTransfer(restarted, blocks[1], TestItem.AddressC, 20);
            reorgHead = ExecuteTransfer(restarted, reorgParent, TestItem.AddressC, 20);
            AssertRecoveredFork(restarted, blocks[1], reorgParent, reorgHead);
        }

        await using IContainer reopened = OpenDiskNode(directory.Path);
        Assert.That(KeptStateResumer.Resume(reopened.Resolve<IBlockTree>(), reopened.Resolve<IStateReader>(), LimboLogs.Instance), Is.EqualTo(3));
        AssertRecoveredFork(reopened, blocks[1], reorgParent, reorgHead);
    }

    private static IContainer OpenDiskNode(string path, CatalogFault? fault = null)
    {
        FlatDbConfig flat = Config();
        flat.Enabled = true;
        flat.TrieWarmerWorkerCount = 0;
        flat.ArenaFileSizeBytes = 1024 * 1024;
        flat.PersistedSnapshotArenaPageCacheBytes = 0;
        ConfigProvider config = new(flat, new InitConfig { BaseDbPath = path });
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new NethermindModule(new ChainSpec { Parameters = new ChainParameters(), Allocations = [] }, config, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, null))
            .AddModule(new TestBlockProcessingModule())
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<IJsonSerializer, EthereumJsonSerializer>()
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Berlin.Instance));
        if (fault is not null)
            builder.AddDecorator<ISnapshotCatalog>((_, catalog) => new InterruptingCatalog(catalog, fault));
        return builder.Build();
    }

    private static Block ExecuteTransfer(IContainer node, Block parent, Address recipient, ulong value)
    {
        Transaction transaction = Build.A.Transaction.WithNonce(parent.Number).WithTo(recipient)
            .WithValue(value).WithGasPrice(1).WithGasLimit(21_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block suggested = Build.A.Block.WithParent(parent).WithTimestamp(parent.Timestamp + 12)
            .WithTransactions(transaction).TestObject;
        return Execute(node, parent, suggested);
    }

    private static Block Execute(IContainer node, Block parent, Block suggested)
    {
        Block processed = node.Resolve<MainProcessingContext>().BranchProcessor.Process(
            parent.Header, [suggested], ProcessingOptions.NoValidation, NullBlockTracer.Instance)[0];
        processed.Header.Hash = processed.Header.CalculateHash();
        IBlockTree tree = node.Resolve<IBlockTree>();
        tree.SuggestBlock(processed);
        Assert.That(tree.TryUpdateMainChain(processed.Header, true, forceUpdateHeadBlock: true, preloadedBlocks: [processed]), Is.True);
        return processed;
    }

    private static void AssertRecoveredFork(IContainer node, Block forkParent, Block fork, Block head)
    {
        IStateReader reader = node.Resolve<IStateReader>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Resolve<IBlockTree>().Head!.Hash, Is.EqualTo(head.Hash));
            Assert.That(node.Resolve<IBlockTree>().FindHeader(fork.Number, BlockTreeLookupOptions.RequireCanonical)!.Hash, Is.EqualTo(fork.Hash));
            Assert.That(reader.GetBalance(head.Header, TestItem.AddressB), Is.EqualTo((UInt256)10));
            Assert.That(reader.GetBalance(head.Header, TestItem.AddressC), Is.EqualTo((UInt256)40));
            Assert.That(reader.GetBalance(forkParent.Header, TestItem.AddressB), Is.EqualTo((UInt256)10));
            Assert.That(node.Resolve<IPersistenceManager>().GetCurrentPersistedStateId().BlockNumber, Is.Zero);
        }
    }

    private sealed class CatalogFault
    {
        public bool Enabled { get; set; }
        public bool Triggered { get; set; }
        public int Attempts { get; set; }
    }

    private sealed class InterruptingCatalog(ISnapshotCatalog inner, CatalogFault fault) : ISnapshotCatalog
    {
        public void Add(CatalogEntry entry)
        {
            // Fail after the second snapshot's files are fsynced, before publishing its catalog row.
            if (fault.Enabled && ++fault.Attempts == 2)
            {
                fault.Triggered = true;
                throw new IOException("Injected catalog publication failure");
            }
            inner.Add(entry);
        }

        public bool Remove(in StateId to, SnapshotTier tier) => inner.Remove(to, tier);
        public IEnumerable<CatalogEntry> Load() => inner.Load();
    }

    /// <summary>A persistence manager over a real persisted tier whose RocksDB state is at <see cref="Block0"/>.</summary>
    private sealed class Session : IDisposable
    {
        private readonly FlatTestContainer _tier;
        private readonly IResourcePool _pool;
        public IPersistenceManager Manager { get; }
        public SnapshotRepository Repository => _tier.Repository;
        public IPersistedSnapshotLoader Loader => _tier.Loader;
        public IStatePersistenceBarrier Barrier { get; } = Substitute.For<IStatePersistenceBarrier>();

        public Session(FlatDbConfig config, string path, IDb catalog)
        {
            IPersistence persistence = Substitute.For<IPersistence>();
            IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
            reader.CurrentState.Returns(Block0);
            persistence.CreateReader().Returns(reader);
            _tier = new FlatTestContainer(config, baseDbPath: path, catalogDb: catalog, configure: builder => builder
                .AddSingleton(persistence)
                .AddSingleton(Barrier)
                .AddSingleton(Substitute.For<IPersistedSnapshotCompactor>()));
            _pool = _tier.Resolve<IResourcePool>();
            Manager = _tier.Resolve<IPersistenceManager>();
        }

        public void AddChain(StateId from, ulong count)
        {
            ulong last = from.BlockNumber + count;
            for (ulong n = from.BlockNumber + 1; n <= last; n++)
            {
                StateId to = State(n);
                Add(from, to);
                from = to;
            }
        }

        public void Add(StateId from, StateId to)
        {
            Snapshot snapshot = _pool.CreateSnapshot(from, to, ResourcePool.Usage.MainBlockProcessing);
            snapshot.Content.Accounts[TestItem.AddressA] = new Account(to.BlockNumber, 100);
            Repository.TryAdd(snapshot, SnapshotTier.InMemoryBase);
            Repository.AddStateId(to);
        }

        public Snapshot? Lease(StateId state) => Repository.TryLeaseInMemoryState(state, SnapshotTier.InMemoryBase, out Snapshot? snapshot) ? snapshot : null;

        public void Dispose() => _tier.Dispose();
    }
}
