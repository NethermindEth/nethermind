// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>
/// The storage tries built in parallel with execution must yield exactly the state root the serial flush yields.
/// </summary>
[TestFixture]
public class FlatParallelStorageRootTests
{
    private const int ContractCount = 6; // above the multi-threaded storage-root threshold

    [TearDown]
    public void TearDown()
    {
        StorageRootBuilder.OnBeforeApplyForTests = null;
        StorageRootBuilder.OnFaultForTests = null;
    }

    // Batch size 1 sends every contract to a background job; the default 128 keeps this small block entirely on the flush path.
    [TestCase(false, false, 1)]
    [TestCase(true, false, 1)]
    [TestCase(false, true, 1)]
    [TestCase(true, true, 1)]
    [TestCase(true, true, 128)]
    public void Parallel_storage_root_matches_serial_flush_and_trie_backend(bool eagerHash, bool viaPrewarmerScope, int batchSize)
    {
        Exception fault = null;
        StorageRootBuilder.OnFaultForTests = e => fault = e;
        Hash256 serialFlat = ComputeRoot(parallel: false, eagerHash: false, viaPrewarmerScope, batchSize);
        long builderWritesBefore = Db.Metrics.ParallelStorageRootWrites;
        Hash256 parallelFlat = ComputeRoot(parallel: true, eagerHash, viaPrewarmerScope, batchSize);
        Hash256 trie = ComputeRootOnTrieBackend();

        Assert.That(fault, Is.Null, () => $"the builder faulted: {fault}");

        if (batchSize == 1)
            Assert.That(Db.Metrics.ParallelStorageRootWrites, Is.GreaterThan(builderWritesBefore), "the builder must have applied the committed writes");
        Assert.That(parallelFlat, Is.EqualTo(serialFlat));
        Assert.That(parallelFlat, Is.EqualTo(trie));
    }

    [Test]
    public void Faulted_builder_falls_back_to_the_serial_flush()
    {
        Hash256 serialFlat = ComputeRoot(parallel: false, eagerHash: false, viaPrewarmerScope: false, batchSize: 1);

        // Let the first job apply, then fail the next one: one trie is left half-built and must be rebuilt from the parent.
        int applies = 0;
        StorageRootBuilder.OnBeforeApplyForTests = () =>
        {
            if (Interlocked.Increment(ref applies) == 2) throw new InvalidOperationException("injected builder fault");
        };
        Hash256 parallelFlat = ComputeRoot(parallel: true, eagerHash: true, viaPrewarmerScope: false, batchSize: 1);

        Assert.That(applies, Is.GreaterThanOrEqualTo(2), "the fault must actually have been injected");
        Assert.That(parallelFlat, Is.EqualTo(serialFlat));
    }

    [TestCase(1)]
    [TestCase(128)]
    public void Repeated_root_flushes_in_one_scope_preserve_later_storage_writes(int batchSize)
    {
        (Hash256 serialFirst, Hash256 serialSecond) = FlushTwice(parallel: false, batchSize);
        (Hash256 parallelFirst, Hash256 parallelSecond) = FlushTwice(parallel: true, batchSize);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialSecond, Is.Not.EqualTo(serialFirst), "the second flush must change the root");
            Assert.That(parallelFirst, Is.EqualTo(serialFirst), "the first builder flush must match serial");
            Assert.That(parallelSecond, Is.EqualTo(serialSecond), "later writes must reach the trie after the builder closes");
        }
    }

    [TestCase(1, false)]
    [TestCase(128, false)]
    [TestCase(1, true)]
    [TestCase(128, true)]
    public void Builder_jobs_restart_for_each_committed_block(int batchSize, bool faultFirstBlock)
    {
        (Hash256[] serialRoots, _) = CommitAcrossScopes(parallel: false, batchSize, faultFirstBlock);
        (Hash256[] parallelRoots, int[] jobs) = CommitAcrossScopes(parallel: true, batchSize, faultFirstBlock);
        TestContext.Out.WriteLine($"Batch {batchSize}: background applies per block = [{string.Join(", ", jobs)}]");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parallelRoots, Is.EqualTo(serialRoots));
            Assert.That(jobs[0], Is.GreaterThan(0), "the first block must exercise background building");
            Assert.That(jobs[1], Is.GreaterThan(0), "committing a block must restore background building within the same scope");
            Assert.That(jobs[2], Is.GreaterThan(0), "a fresh scope must restore background building");
            Assert.That(serialRoots[1], Is.Not.EqualTo(serialRoots[0]));
            Assert.That(serialRoots[2], Is.Not.EqualTo(serialRoots[1]));
        }
    }

    [Test]
    public async Task Creating_storage_batches_does_not_wait_for_background_building([Values] bool faultWorker)
    {
        ConfigProvider config = new();
        IFlatDbConfig flat = config.GetConfig<IFlatDbConfig>();
        flat.Enabled = true;
        flat.ParallelStorageRoot = true;
        flat.ParallelStorageRootBatchSize = 1;
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule(config)).Build();
        IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().GlobalWorldState;
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        StorageRootBuilder.OnBeforeApplyForTests = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("The test did not release the builder.");
            if (faultWorker) throw new InvalidOperationException("injected finalization fault");
        };

        Hash256 root;
        bool createdBeforeRelease;
        using (IWorldStateScopeProvider.IScope scope = provider.BeginScope(IWorldState.PreGenesis, new LocalMetrics()))
        {
            IWorldStateScopeProvider.IStorageTree tree = scope.CreateStorageTree(TestItem.AddressA);
            tree.HintSet(UInt256.Zero, UInt256.One);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "the builder must be running");
            using (IWorldStateScopeProvider.IWorldStateWriteBatch write = scope.StartWriteBatch(1))
            {
                Task<IWorldStateScopeProvider.IStorageWriteBatch> create = Task.Run(() => write.CreateStorageWriteBatch(TestItem.AddressA, 1));
                try
                {
                    createdBeforeRelease = await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(3))) == create;
                }
                finally
                {
                    release.Set();
                }
                using IWorldStateScopeProvider.IStorageWriteBatch storage = await create;
                write.Set(TestItem.AddressA, Account.TotallyEmpty);
                storage.Set(UInt256.Zero, UInt256.One);
            }
            scope.UpdateRootHash();
            scope.Commit(0);
            root = scope.RootHash;
        }

        flat.VerifyWithTrie = true;
        using (IWorldStateScopeProvider.IScope reopened = provider.BeginScope(Build.A.BlockHeader.WithNumber(0).WithStateRoot(root).TestObject, new LocalMetrics()))
        {
            reopened.CreateStorageTree(TestItem.AddressA).Get(UInt256.Zero, out UInt256 value);
            Assert.That(value, Is.EqualTo(UInt256.One));
        }
        Assert.That(createdBeforeRelease, Is.True, "creating batches must not join builders before the parallel flush starts");
    }

    private static (Hash256[] Roots, int[] Jobs) CommitAcrossScopes(bool parallel, int batchSize, bool faultFirstBlock)
    {
        ConfigProvider config = new();
        IFlatDbConfig flat = config.GetConfig<IFlatDbConfig>();
        flat.Enabled = true;
        flat.ParallelStorageRoot = parallel;
        flat.ParallelStorageRootBatchSize = batchSize;
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule(config)).Build();
        IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().GlobalWorldState;
        using ILifetimeScope processing = container.BeginLifetimeScope(builder => builder.RegisterInstance(provider).As<IWorldStateScopeProvider>());
        IWorldState state = processing.Resolve<IWorldState>();
        Hash256[] roots = new Hash256[3];
        int[] jobs = new int[3];
        Exception fault = null;
        StorageRootBuilder.OnFaultForTests = e => Interlocked.CompareExchange(ref fault, e, null);
        int phase = 0;
        int injectedFault = 0;
        StorageRootBuilder.OnBeforeApplyForTests = () =>
        {
            int block = Volatile.Read(ref phase);
            Interlocked.Increment(ref jobs[block]);
            if (faultFirstBlock && block == 0 && Interlocked.Exchange(ref injectedFault, 1) == 0)
                throw new InvalidOperationException("injected first-block builder fault");
        };

        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(TestItem.AddressA, UInt256.One);
            ApplyBlock(0);
            ApplyBlock(1);
        }

        using (state.BeginScope(Build.A.BlockHeader.WithNumber(1).WithStateRoot(roots[1]).TestObject))
            ApplyBlock(2);

        if (parallel && faultFirstBlock)
            Assert.That(fault, Is.TypeOf<InvalidOperationException>());
        else
            Assert.That(fault, Is.Null, () => $"the builder faulted: {fault}");
        flat.VerifyWithTrie = true;
        using (state.BeginScope(Build.A.BlockHeader.WithNumber(2).WithStateRoot(roots[2]).TestObject))
        {
            for (ulong index = 0; index < (ulong)batchSize; index++)
            {
                state.Get(new StorageCell(TestItem.AddressA, new UInt256(index)), out UInt256 value);
                Assert.That(value, Is.EqualTo(new UInt256(3)));
            }
        }
        return (roots, jobs);

        void ApplyBlock(int block)
        {
            Volatile.Write(ref phase, block);
            for (ulong index = 0; index < (ulong)batchSize; index++)
                state.Set(new StorageCell(TestItem.AddressA, new UInt256(index)), new UInt256((ulong)block + 1));
            state.Commit(Prague.Instance, commitRoots: false);
            state.Commit(Prague.Instance);
            state.CommitTree((ulong)block);
            roots[block] = state.StateRoot;
        }
    }

    private static (Hash256 First, Hash256 Second) FlushTwice(bool parallel, int batchSize)
    {
        ConfigProvider config = new();
        IFlatDbConfig flat = config.GetConfig<IFlatDbConfig>();
        flat.Enabled = true;
        flat.ParallelStorageRoot = parallel;
        flat.ParallelStorageRootBatchSize = batchSize;
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule(config)).Build();
        IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().GlobalWorldState;
        using ILifetimeScope processing = container.BeginLifetimeScope(builder => builder.RegisterInstance(provider).As<IWorldStateScopeProvider>());
        IWorldState state = processing.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(TestItem.AddressA, UInt256.One);
        for (ulong index = 0; index < (ulong)batchSize; index++)
            state.Set(new StorageCell(TestItem.AddressA, new UInt256(index)), UInt256.One);
        state.Commit(Frontier.Instance);
        state.RecalculateStateRoot();
        Hash256 first = state.StateRoot;

        state.Set(new StorageCell(TestItem.AddressA, UInt256.Zero), new UInt256(2));
        state.Commit(Frontier.Instance);
        state.RecalculateStateRoot();
        return (first, state.StateRoot);
    }

    private static Hash256 ComputeRoot(bool parallel, bool eagerHash, bool viaPrewarmerScope, int batchSize)
    {
        ConfigProvider configProvider = new();
        IFlatDbConfig flatConfig = configProvider.GetConfig<IFlatDbConfig>();
        flatConfig.Enabled = true;
        flatConfig.ParallelStorageRoot = parallel;
        flatConfig.ParallelStorageRootEagerHash = eagerHash;
        flatConfig.ParallelStorageRootThreads = 3;
        flatConfig.ParallelStorageRootBatchSize = batchSize;
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule(configProvider)).Build();
        IWorldStateScopeProvider scopeProvider = container.Resolve<IWorldStateManager>().GlobalWorldState;
        if (viaPrewarmerScope)
        {
            // The node's main scope is decorated this way; the wrapper must forward the committed-value hints.
            PrewarmerState mainScopeState = new(new PreBlockCaches(TestPreBlockCachesConfig.Small), isPrewarmer: false);
            scopeProvider = new PrewarmerScopeProvider(scopeProvider, mainScopeState, LimboLogs.Instance);
        }
        WorldState worldState = new(scopeProvider, LimboLogs.Instance);
        return RunBlock(worldState);
    }

    private static Hash256 ComputeRootOnTrieBackend() => RunBlock(TestWorldStateFactory.CreateForTest());

    /// <summary>
    /// A pre-block state of several contracts, then one block of "transactions" each committed the way the transaction
    /// processor commits them: writes to many contracts, a slot rewritten across transactions, a slot zeroed, a slot
    /// written back to its pre-block value, a contract cleared and deleted, and an untouched pre-existing slot.
    /// </summary>
    private static Hash256 RunBlock(IWorldState worldState)
    {
        Address[] contracts = new Address[ContractCount];
        for (int i = 0; i < ContractCount; i++) contracts[i] = new Address(Keccak.Compute(new[] { (byte)(i + 1) }).Bytes[..20]);

        BlockHeader genesis;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            for (int i = 0; i < ContractCount; i++)
            {
                worldState.CreateAccount(contracts[i], 1);
                worldState.Set(new StorageCell(contracts[i], 1), new UInt256(100 + (ulong)i));
            }
            worldState.Commit(Frontier.Instance, commitRoots: false);
            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            genesis = Build.A.BlockHeader.WithNumber(0).WithStateRoot(worldState.StateRoot).TestObject;
        }

        using (worldState.BeginScope(genesis))
        {
            for (int i = 0; i < ContractCount; i++) worldState.Set(new StorageCell(contracts[i], 2), new UInt256(7 * (ulong)i + 1));
            worldState.Commit(Frontier.Instance, commitRoots: false);

            worldState.Set(new StorageCell(contracts[0], 2), new UInt256(999));
            worldState.Set(new StorageCell(contracts[1], 1), UInt256.Zero);
            worldState.Set(new StorageCell(contracts[2], 3), new UInt256(5));
            worldState.Commit(Frontier.Instance, commitRoots: false);

            worldState.ClearStorage(contracts[3]);
            worldState.DeleteAccount(contracts[3]);
            worldState.Set(new StorageCell(contracts[4], 2), new UInt256(42));
            worldState.Set(new StorageCell(contracts[4], 2), new UInt256(43));
            worldState.Commit(Frontier.Instance, commitRoots: false);

            worldState.Set(new StorageCell(contracts[5], 1), new UInt256(105));
            worldState.Set(new StorageCell(contracts[0], 4), UInt256.One);
            worldState.Commit(Frontier.Instance, commitRoots: false);

            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(1);
            return worldState.StateRoot;
        }
    }
}
