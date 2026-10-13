// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>
/// The in-flight read-ahead: storage discovery for the transaction block processing is executing, started by block
/// processing's reads past the pre-block caches, seeded with what the block's earlier transactions wrote, retried with
/// a zero placeholder, and stopped and joined with the block's session.
/// </summary>
[TestFixture]
public class PrewarmerReadAheadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Address LongLoop = new("0x00000000000000000000000000000000004e4d3f");
    private static readonly StorageCell LongLoopFinished = new(LongLoop, 2);
    // Reads slot 1, counts down from 2^32 - 1, then reads slot 2.
    private static readonly byte[] LongLoopCode =
    [
        0x60, 0x01, 0x54, 0x50, 0x63, 0xFF, 0xFF, 0xFF, 0xFF, 0x5B, 0x60, 0x01, 0x90, 0x03, 0x80, 0x60, 0x09, 0x57, 0x50,
        0x60, 0x02, 0x54, 0x50, 0x00
    ];
    private static readonly IReleaseSpec Spec = Osaka.Instance;
    // Gas for minutes of the long loop even in Release; a block that large admits the run to discovery's rounds.
    private const ulong LoopGas = 200_000_000_000;

    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private BlockHeader _parent = null!;
    private PreBlockCaches _caches = null!;

    [SetUp]
    public void Setup()
    {
        KnownParents parents = new();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Spec))
            .AddSingleton<IStateHeaderProvider>(parents)
            .Build();

        IMainProcessingModule[] mainModules = _container.Resolve<IMainProcessingModule[]>();
        IWorldStateManager worldStateManager = _container.Resolve<IWorldStateManager>();
        IWorldStateScopeProvider scopeProvider = worldStateManager.GlobalWorldState;
        _scope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(worldStateManager).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(mainModules);
        });

        IWorldState worldState = _scope.Resolve<IWorldState>();
        Hash256 genesisRoot;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            foreach (Address funded in (Address[])[TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD])
            {
                worldState.CreateAccount(funded, 1_000.Ether);
            }

            ColdTailContracts.Deploy(worldState, Spec);
            worldState.CreateAccount(LongLoop, 0);
            worldState.InsertCode(LongLoop, Keccak.Compute(LongLoopCode), LongLoopCode, Spec);
            worldState.Set(new StorageCell(LongLoop, 1), 1);
            worldState.Set(LongLoopFinished, 1);
            worldState.Commit(Spec);
            worldState.CommitTree(0);
            genesisRoot = worldState.StateRoot;
        }

        _parent = parents.Add(Build.A.BlockHeader.WithNumber(0).WithStateRoot(genesisRoot).WithGasLimit(30_000_000)
            .WithHash(Build.A.BlockHeader.TestObject.ParentHash!).TestObject);
        _caches = _scope.Resolve<PreBlockCaches>();
    }

    [TearDown]
    public void TearDown()
    {
        _scope?.Dispose();
        _container?.Dispose();
    }

    [Test]
    public void Discovery_keeps_a_candidate_block_processing_is_executing()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction walk = WalkerCall(TestItem.PrivateKeyA, 0, 0x4e4d, 4);
        Block block = BuildBlock(walk);
        preWarmer.OnBeforeTxExecution();

        preWarmer.DiscoverAndWarmStorage([(0, walk)], block, Spec, null, CancellationToken.None);

        Assert.That(_caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Walker, 0x4e4d + 3), out _), Is.True,
            "a transaction block processing is executing is still worth reading ahead of it");
    }

    [Test]
    public void Discovery_drops_a_candidate_block_processing_has_finished()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction walk = WalkerCall(TestItem.PrivateKeyA, 0, 0x4e4d, 4);
        Block block = BuildBlock(walk, Transfer(TestItem.PrivateKeyB, 0));
        preWarmer.OnBeforeTxExecution();
        preWarmer.OnBeforeTxExecution();

        preWarmer.DiscoverAndWarmStorage([(0, walk)], block, Spec, null, CancellationToken.None);

        Assert.That(_caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Walker, 0x4e4d), out _), Is.False);
    }

    [Test]
    public void Switched_off_discovery_leaves_a_candidate_block_processing_has_started()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: false);
        Transaction walk = WalkerCall(TestItem.PrivateKeyA, 0, 0x4e4d, 4);
        Block block = BuildBlock(walk);
        preWarmer.OnBeforeTxExecution();

        preWarmer.DiscoverAndWarmStorage([(0, walk)], block, Spec, null, CancellationToken.None);

        Assert.That(_caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Walker, 0x4e4d), out _), Is.False);
    }

    [Test]
    public void A_run_stops_once_block_processing_finishes_its_transaction()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction loop = Build.A.Transaction.WithGasLimit(LoopGas).WithTo(LongLoop).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = BuildBlock(LoopGas, loop, Transfer(TestItem.PrivateKeyB, 0));
        using CancellationTokenSource cancellation = new();

        Task discovery = Task.Run(() => preWarmer.DiscoverAndWarmStorage([(0, loop)], block, Spec, null, cancellation.Token));
        try
        {
            Assert.That(SpinWait.SpinUntil(() => _caches.StateCache.TryGetValue(LongLoop, out _), Timeout), Is.True,
                "precondition: the run has entered the loop's call");
            preWarmer.OnBeforeTxExecution();
            Assert.That(discovery.Wait(TimeSpan.FromSeconds(1)), Is.False, "a run of the transaction block processing is executing goes on");
            preWarmer.OnBeforeTxExecution();

            Assert.That(discovery.Wait(TimeSpan.FromSeconds(5)), Is.True, "the run stops once block processing has finished the transaction");
            Assert.That(_caches.StorageCache.TryGetValue(LongLoopFinished, out _), Is.False);
        }
        finally
        {
            cancellation.Cancel();
            discovery.Wait(Timeout);
        }
    }

    /// <summary>
    /// A claim reverts at its first slot unless that slot is still zero, so a placeholder of 1 or 0x0101..01 finds one
    /// slot per round; a run with 0 finds the whole range in one.
    /// </summary>
    [Test]
    public void A_run_that_reverts_with_every_placeholder_is_repeated_with_zero()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction claim = BatchClaim(TestItem.PrivateKeyA, 0, 8);
        Block block = BuildBlock(claim);

        preWarmer.DiscoverAndWarmStorage([(0, claim)], block, Spec, null, CancellationToken.None);

        StorageCell last = ColdTailContracts.BatchSlot(ColdTailContracts.BatchCounter, 7);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_caches.StorageCache.TryGetValue(in last, out UInt256 value), Is.True, "the last slot of the claim is found");
            Assert.That(value, Is.EqualTo(UInt256.Zero), "at its parent value, never the placeholder");
        }
    }

    /// <summary>
    /// The second claim of a sender reads the range the counter the first one moved points at. Seeded with the counter
    /// block processing left, the read-ahead finds that range; the caches get its slots, and the counter, only at their
    /// parent values, and the seeds are committed without roots.
    /// </summary>
    [Test]
    public void A_run_is_seeded_with_what_the_blocks_earlier_transactions_wrote([Values] bool seeded)
    {
        CommitCountingEnvs envs = new(_scope.Resolve<PrewarmerEnvFactory>(), _caches);
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, policy: envs);
        Transaction first = BatchClaim(TestItem.PrivateKeyA, 0, 6);
        Transaction second = BatchClaim(TestItem.PrivateKeyA, 1, 6);
        Block block = BuildBlock(first, second);
        BlockFootprints footprints = new(block);
        UInt256 moved = ColdTailContracts.BatchCounter + 6;
        preWarmer.OnBeforeTxExecution();
        footprints.QueueExecuted(0, new[] { (new StorageCell(ColdTailContracts.Batch, 0), moved) });
        footprints.ApplyExecuted();
        preWarmer.OnBeforeTxExecution();

        preWarmer.DiscoverAndWarmStorage([(1, second)], block, Spec, null, CancellationToken.None, footprints: seeded ? footprints : null);

        bool found = _caches.StorageCache.TryGetValue(ColdTailContracts.BatchSlot(moved, 5), out UInt256 value);
        bool counterCached = _caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Batch, 0), out UInt256 counter);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.EqualTo(seeded), "the second claim's own range is found only on the counter the first one left");
            if (found) Assert.That(value, Is.EqualTo(UInt256.Zero));
            if (counterCached) Assert.That(counter, Is.EqualTo(ColdTailContracts.BatchCounter), "the seed never reaches the caches");
            Assert.That(envs.CommitsWithRoots, Is.Zero, "a seeded run commits nothing with roots");
            Assert.That(envs.CommitsWithoutRoots, seeded ? Is.GreaterThan(0) : Is.Zero);
            Assert.That(footprints.Get(0), Is.Null);
            Assert.That(footprints.Get(1), Is.Null, "a read-ahead records no footprint");
        }
    }

    /// <summary>Seeds may be wrong, as a footprint's prediction can be; the caches still only ever get parent values.</summary>
    [Test]
    public void A_wrong_seed_costs_precision_never_a_cached_value()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction first = BatchClaim(TestItem.PrivateKeyA, 0, 6);
        Transaction second = BatchClaim(TestItem.PrivateKeyA, 1, 6);
        Block block = BuildBlock(first, second);
        BlockFootprints footprints = new(block);
        preWarmer.OnBeforeTxExecution();
        // One behind what block processing would leave, and a slot of the range the second claim checks set to non-zero.
        footprints.QueueExecuted(0, new[]
        {
            (new StorageCell(ColdTailContracts.Batch, 0), ColdTailContracts.BatchCounter + 5),
            (ColdTailContracts.BatchSlot(ColdTailContracts.BatchCounter + 5, 1), (UInt256)1)
        });
        footprints.ApplyExecuted();
        preWarmer.OnBeforeTxExecution();

        preWarmer.DiscoverAndWarmStorage([(1, second)], block, Spec, null, CancellationToken.None, footprints: footprints);

        Assert.That(EntriesNotAtTheParent(), Is.Empty);
    }

    [Test]
    public void Block_processings_cold_reads_start_one_read_ahead_of_the_transaction_it_executes()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, coldReads: 3);
        Block block = BuildBlock(WalkerCall(TestItem.PrivateKeyA, 0, 0x5000, 6), Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        using IDisposable session = OpenSession(preWarmer, block);
        _caches.StorageCache.Clear();

        _caches.ReportConsumerColdRead();
        preWarmer.OnBeforeTxExecution();
        _caches.ReportConsumerColdRead();
        _caches.ReportConsumerColdRead();
        int belowThreshold = preWarmer.ReadAheadsStarted;
        _caches.ReportConsumerColdRead();
        int atThreshold = preWarmer.ReadAheadsStarted;
        for (int i = 0; i < 10; i++) _caches.ReportConsumerColdRead();
        bool idle = SpinWait.SpinUntil(() => preWarmer.ReadAheadsRunning == 0, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(belowThreshold, Is.Zero, "reads before the first transaction and below the threshold start nothing");
            Assert.That(atThreshold, Is.EqualTo(1));
            Assert.That(preWarmer.ReadAheadsStarted, Is.EqualTo(1), "once per transaction");
            Assert.That(idle, Is.True);
            Assert.That(_caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Walker, 0x5005), out _), Is.True,
                "the read-ahead read the transaction's slots");
        }
    }

    [Test]
    public void The_sessions_end_stops_and_joins_its_read_ahead()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        // A creation whose init code loops for minutes: the up-front discovery takes no creation, so the read-ahead runs it.
        Transaction loop = Build.A.Transaction.WithGasLimit(LoopGas).WithCode(LongLoopCode).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = BuildBlock(LoopGas, loop, Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        IWorldState worldState = _scope.Resolve<IWorldState>();
        using (worldState.BeginScope(_parent))
        {
            IDisposable? session = preWarmer.PreWarmCaches(block, _parent, Spec);
            Assert.That(session, Is.Not.Null);
            preWarmer.OnBeforeTxExecution();
            _caches.ReportConsumerColdRead();
            Thread.Sleep(200);
            bool running = preWarmer.ReadAheadsRunning == 1;

            Task end = Task.Run(() => session!.Dispose());
            bool ended = end.Wait(TimeSpan.FromSeconds(10));
            int accounts = _caches.StateCache.SnapshotForTests().Count;
            int slots = _caches.StorageCache.SnapshotForTests().Count;
            _caches.ReportConsumerColdRead();
            Thread.Sleep(200);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(running, Is.True, "precondition: the read-ahead is in its run when the session ends");
                Assert.That(ended, Is.True, "the session's end stops the run rather than waiting it out");
                Assert.That(preWarmer.ReadAheadsRunning, Is.Zero);
                Assert.That(_caches.ColdReadObserver, Is.Null, "nothing listens to block processing's reads past the session");
                Assert.That(preWarmer.ReadAheadsStarted, Is.EqualTo(1), "no read-ahead starts past the session");
                Assert.That(_caches.StateCache.SnapshotForTests(), Has.Count.EqualTo(accounts), "nothing is written past the session");
                Assert.That(_caches.StorageCache.SnapshotForTests(), Has.Count.EqualTo(slots), "nothing is written past the session");
            }
        }
    }

    [Test]
    public void A_read_ahead_that_fails_leaves_block_processing_alone()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, policy: new ThrowingEnvs());
        Block block = BuildBlock(WalkerCall(TestItem.PrivateKeyA, 0, 0x5000, 6), Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        IWorldState worldState = _scope.Resolve<IWorldState>();
        using (worldState.BeginScope(_parent))
        {
            IDisposable? session = preWarmer.PreWarmCaches(block, _parent, Spec);
            preWarmer.OnBeforeTxExecution();

            Assert.DoesNotThrow(_caches.ReportConsumerColdRead);
            Assert.That(SpinWait.SpinUntil(() => preWarmer.ReadAheadsRunning == 0, Timeout), Is.True);
            Assert.DoesNotThrow(() => session?.Dispose());
            Assert.That(preWarmer.ReadAheadsStarted, Is.EqualTo(1));
        }
    }

    [Test]
    public void Switched_off_it_listens_to_no_read([Values] bool readAhead)
    {
        BlocksConfig config = new()
        {
            PreWarming = PreWarmMode.Block,
            PreWarmStateConcurrency = 2,
            PreWarmInFlightReadAhead = readAhead
        };
        using BlockCachePreWarmer preWarmer = new(_scope.Resolve<PrewarmerEnvFactory>(), config, _scope.Resolve<NodeStorageCache>(), _caches, LimboLogs.Instance);
        Block block = BuildBlock(WalkerCall(TestItem.PrivateKeyA, 0, 0x5000, 6), Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        bool listening;
        using (OpenSession(preWarmer, block))
        {
            listening = _caches.ColdReadObserver is not null;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(listening, Is.EqualTo(readAhead));
            Assert.That(_caches.ColdReadObserver, Is.Null);
        }
    }

    [Test]
    public void The_config_defaults_start_at_the_thirty_second_cold_read()
    {
        BlocksConfig config = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.PreWarmInFlightReadAhead, Is.True);
            Assert.That(config.PreWarmInFlightReadAheadColdReads, Is.EqualTo(BlockCachePreWarmer.DefaultReadAheadColdReads));
            Assert.That(BlockCachePreWarmer.DefaultReadAheadColdReads, Is.EqualTo(32));
        }
    }

    /// <summary>Opens the block's session and waits for its warm pass, leaving the session, and its read-ahead, open.</summary>
    private IDisposable OpenSession(BlockCachePreWarmer preWarmer, Block block)
    {
        IWorldState worldState = _scope.Resolve<IWorldState>();
        using (worldState.BeginScope(_parent))
        {
            IDisposable? session = preWarmer.PreWarmCaches(block, _parent, Spec);
            Assert.That(session, Is.Not.Null, "precondition: the block is warmed");
            ((PrewarmingSession)session!).WaitForCompletion();
            return session;
        }
    }

    private List<string> EntriesNotAtTheParent()
    {
        IStateReader reader = _container.Resolve<IWorldStateManager>().GlobalStateReader;
        List<string> wrong = [];
        foreach ((StorageCell cell, UInt256 value) in _caches.StorageCache.SnapshotForTests())
        {
            reader.GetStorage(_parent, cell.Address, in cell.Index, out UInt256 parent);
            if (value != parent) wrong.Add($"{cell}: {value} cached, {parent} at the parent");
        }

        return wrong;
    }

    private BlockCachePreWarmer CreatePreWarmer(bool readAhead, int coldReads = 0, IPooledObjectPolicy<IPrewarmerEnv>? policy = null) =>
        new(policy ?? new BlockCachePreWarmer.ReadOnlyTxProcessingEnvPooledObjectPolicy(_scope.Resolve<PrewarmerEnvFactory>(), _caches),
            minPoolSize: 4,
            concurrency: 2,
            parallelExecutionBatchRead: true,
            _scope.Resolve<NodeStorageCache>(),
            _caches,
            LimboLogs.Instance,
            readAhead: readAhead,
            readAheadColdReads: coldReads);

    private static Block BuildBlock(params Transaction[] transactions) => BuildBlock(30_000_000, transactions);

    private static Block BuildBlock(ulong gasLimit, params Transaction[] transactions) =>
        Build.A.Block.WithNumber(1).WithGasLimit(gasLimit).WithTransactions(transactions).TestObject;

    private static Transaction WalkerCall(PrivateKey sender, ulong nonce, UInt256 start, int count) =>
        Call(sender, nonce, ColdTailContracts.Walker, 1_000_000, ColdTailContracts.Words(start, (ulong)count));

    private static Transaction BatchClaim(PrivateKey sender, ulong nonce, int count) =>
        Call(sender, nonce, ColdTailContracts.Batch, 2_100_000, ColdTailContracts.Word((ulong)count));

    private static Transaction Call(PrivateKey sender, ulong nonce, Address to, ulong gasLimit, byte[] data) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(to).WithGasLimit(gasLimit).WithData(data).SignedAndResolved(sender).TestObject;

    private static Transaction Transfer(PrivateKey sender, ulong nonce) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(TestItem.AddressF).WithValue(1.Wei).SignedAndResolved(sender).TestObject;

    private sealed class KnownParents : IStateHeaderProvider
    {
        private readonly Dictionary<Hash256, BlockHeader> _headers = [];

        public BlockHeader Add(BlockHeader header)
        {
            _headers[header.Hash!] = header;
            return header;
        }

        public BlockHeader? FindParentHeader(BlockHeader target) => _headers.GetValueOrDefault(target.ParentHash!);
        public ulong FinalizedBlockNumber => 0;
        public BlockHeader? GetFinalizedHeader(ulong blockNumber) => null;
    }

    /// <summary>Envs whose scopes count the commits made through their world state, with roots and without.</summary>
    private sealed class CommitCountingEnvs(PrewarmerEnvFactory factory, PreBlockCaches caches) : IPooledObjectPolicy<IPrewarmerEnv>
    {
        public int CommitsWithRoots;
        public int CommitsWithoutRoots;

        public IPrewarmerEnv Create() => new Env(factory.Create(caches), this);

        public bool Return(IPrewarmerEnv obj) => true;

        private sealed class Env(IPrewarmerEnv inner, CommitCountingEnvs owner) : IPrewarmerEnv
        {
            public ReadOnlySpan<IHasAccessList> SystemAccessLists => inner.SystemAccessLists;

            FootprintRecorder? IPrewarmerEnv.Recorder => inner.Recorder;

            public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                scope = inner.TryBuild(baseBlock, out IReadOnlyTxProcessingScope? built) ? new Scope(built, owner) : null;
                return scope is not null;
            }

            public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                scope = inner.TryBuildAtTarget(targetBlock, out IReadOnlyTxProcessingScope? built) ? new Scope(built, owner) : null;
                return scope is not null;
            }

            public void Dispose() => inner.Dispose();
        }

        private sealed class Scope(IReadOnlyTxProcessingScope inner, CommitCountingEnvs owner) : IReadOnlyTxProcessingScope
        {
            public Evm.TransactionProcessing.ITransactionProcessor TransactionProcessor => inner.TransactionProcessor;
            public IWorldState WorldState { get; } = new Counting(inner.WorldState, owner);
            public void Reset() => inner.Reset();
            public void Dispose() => inner.Dispose();
        }

        private sealed class Counting(IWorldState state, CommitCountingEnvs owner) : WorldStateDecorator(state)
        {
            public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
            {
                if (commitRoots) Interlocked.Increment(ref owner.CommitsWithRoots);
                else Interlocked.Increment(ref owner.CommitsWithoutRoots);
                base.Commit(releaseSpec, tracer, isGenesis, commitRoots);
            }
        }
    }

    /// <summary>Envs whose every scope fails to build.</summary>
    private sealed class ThrowingEnvs : IPooledObjectPolicy<IPrewarmerEnv>
    {
        public IPrewarmerEnv Create() => new Env();

        public bool Return(IPrewarmerEnv obj) => true;

        private sealed class Env : IPrewarmerEnv
        {
            public ReadOnlySpan<IHasAccessList> SystemAccessLists => [];

            public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) =>
                throw new InvalidOperationException("The env fails to build.");

            public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) =>
                throw new InvalidOperationException("The env fails to build.");

            public void Dispose() { }
        }
    }
}
