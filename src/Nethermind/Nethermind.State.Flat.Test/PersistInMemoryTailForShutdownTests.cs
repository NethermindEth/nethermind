// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
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

        Assert.That(session.Manager.PersistInMemoryTailForShutdown(CancellationToken.None), Is.EqualTo(2));
        Assert.That(session.Repository.HasBasePersistedSnapshot(State(4)), Is.True);
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
            if (reason != "cancelled") Assert.That(session.Barrier.ReceivedCalls(), Is.Empty, "nothing to persist, nothing to flush");
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

    /// <summary>A persistence manager over a real persisted tier whose RocksDB state is at <see cref="Block0"/>.</summary>
    private sealed class Session : IDisposable
    {
        private readonly FlatTestContainer _tier;
        private readonly ResourcePool _pool;
        public PersistenceManager Manager { get; }
        public SnapshotRepository Repository => _tier.Repository;
        public IPersistedSnapshotLoader Loader => _tier.Loader;
        public IStatePersistenceBarrier Barrier { get; } = Substitute.For<IStatePersistenceBarrier>();

        public Session(FlatDbConfig config, string path, IDb catalog)
        {
            _tier = new FlatTestContainer(config, baseDbPath: path, catalogDb: catalog);
            _pool = new ResourcePool(config);
            IPersistence persistence = Substitute.For<IPersistence>();
            IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
            reader.CurrentState.Returns(Block0);
            persistence.CreateReader().Returns(reader);
            Manager = new PersistenceManager(config, _tier.Resolve<ICompactionSchedule>(), UnavailableStateHeaderProvider.Instance, persistence,
                Repository, Barrier, LimboLogs.Instance, Substitute.For<IPersistedSnapshotCompactor>(), _tier.Loader, Substitute.For<IProcessExitSource>());
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

        public void Dispose()
        {
            Manager.Dispose();
            _tier.Dispose();
        }
    }
}
