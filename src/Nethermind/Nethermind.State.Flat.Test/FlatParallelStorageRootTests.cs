// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
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
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// The storage tries built in parallel with execution must yield exactly the state root the serial flush yields.
/// </summary>
[TestFixture]
public class FlatParallelStorageRootTests
{
    private const int ContractCount = 6; // above the multi-threaded storage-root threshold
    private const int SlotCount = 8;

    [TearDown]
    public void TearDown() => StorageRootBuilder.OnBeforeApplyForTests = null;

    [TestCase(false, false, true)]
    [TestCase(true, false, true)]
    [TestCase(false, true, true)]
    [TestCase(true, true, true)]
    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, false)]
    public void Parallel_storage_root_matches_serial_flush_and_trie_backend(bool eagerHash, bool viaPrewarmerScope, bool deferStorageTrieCommit)
    {
        Hash256 serialFlat = ComputeRoot(parallel: false, eagerHash: false, viaPrewarmerScope, deferStorageTrieCommit);
        long builderWritesBefore = Db.Metrics.ParallelStorageRootWrites;
        Hash256 parallelFlat = ComputeRoot(parallel: true, eagerHash, viaPrewarmerScope, deferStorageTrieCommit);
        Hash256 trie = ComputeRootOnTrieBackend();

        Assert.That(Db.Metrics.ParallelStorageRootWrites, Is.GreaterThan(builderWritesBefore), "the builder must have applied the committed writes");
        Assert.That(parallelFlat, Is.EqualTo(serialFlat));
        Assert.That(parallelFlat, Is.EqualTo(trie));
    }

    [Test]
    public void Faulted_builder_falls_back_to_the_serial_flush([Values] bool deferStorageTrieCommit)
    {
        Hash256 serialFlat = ComputeRoot(parallel: false, eagerHash: false, viaPrewarmerScope: false, deferStorageTrieCommit);

        // Let the first write apply, then fail the next one: the trie the builder wrote must be rebuilt from the parent.
        int applies = 0;
        StorageRootBuilder.OnBeforeApplyForTests = () =>
        {
            if (Interlocked.Increment(ref applies) == 2) throw new InvalidOperationException("injected builder fault");
        };
        Hash256 parallelFlat = ComputeRoot(parallel: true, eagerHash: true, viaPrewarmerScope: false, deferStorageTrieCommit);

        Assert.That(applies, Is.GreaterThanOrEqualTo(2), "the fault must actually have been injected");
        Assert.That(parallelFlat, Is.EqualTo(serialFlat));
    }

    [Test]
    public void Repeated_root_flushes_in_one_scope_preserve_later_storage_writes([Values] bool deferStorageTrieCommit)
    {
        (Hash256 serialFirst, Hash256 serialSecond) = FlushTwice(parallel: false, deferStorageTrieCommit);
        (Hash256 parallelFirst, Hash256 parallelSecond) = FlushTwice(parallel: true, deferStorageTrieCommit);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialSecond, Is.Not.EqualTo(serialFirst), "the second flush must change the root");
            Assert.That(parallelFirst, Is.EqualTo(serialFirst), "the first builder flush must match serial");
            Assert.That(parallelSecond, Is.EqualTo(serialSecond), "later writes must reach the trie after the builder drains");
        }
    }

    [Test]
    public void Later_blocks_and_a_new_scope_match_the_serial_flush([Values] bool faultFirstBlock, [Values] bool deferStorageTrieCommit)
    {
        Hash256[] serialRoots = CommitAcrossScopes(parallel: false, faultFirstBlock, deferStorageTrieCommit);
        Hash256[] parallelRoots = CommitAcrossScopes(parallel: true, faultFirstBlock, deferStorageTrieCommit);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parallelRoots, Is.EqualTo(serialRoots));
            Assert.That(serialRoots[1], Is.Not.EqualTo(serialRoots[0]));
            Assert.That(serialRoots[2], Is.Not.EqualTo(serialRoots[1]));
        }
    }

    // Two blocks in one scope, where the builder serves only the first, then one block in a fresh scope with its own
    // builder. A scope with VerifyWithTrie then reads every slot through the committed trie.
    private static Hash256[] CommitAcrossScopes(bool parallel, bool faultFirstBlock, bool deferStorageTrieCommit)
    {
        ConfigProvider config = new();
        IFlatDbConfig flat = config.GetConfig<IFlatDbConfig>();
        flat.Enabled = true;
        flat.ParallelStorageRoot = parallel;
        flat.DeferStorageTrieCommit = deferStorageTrieCommit;
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule(config)).Build();
        IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().GlobalWorldState;
        using ILifetimeScope processing = container.BeginLifetimeScope(builder => builder.RegisterInstance(provider).As<IWorldStateScopeProvider>());
        IWorldState state = processing.Resolve<IWorldState>();
        Hash256[] roots = new Hash256[3];
        int injectedFault = 0;
        StorageRootBuilder.OnBeforeApplyForTests = () =>
        {
            if (faultFirstBlock && Interlocked.Exchange(ref injectedFault, 1) == 0)
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

        flat.VerifyWithTrie = true;
        using (state.BeginScope(Build.A.BlockHeader.WithNumber(2).WithStateRoot(roots[2]).TestObject))
        {
            for (ulong index = 0; index < SlotCount; index++)
            {
                state.Get(new StorageCell(TestItem.AddressA, new UInt256(index)), out UInt256 value);
                Assert.That(value, Is.EqualTo(new UInt256(3)));
            }
        }
        return roots;

        void ApplyBlock(int block)
        {
            for (ulong index = 0; index < SlotCount; index++)
                state.Set(new StorageCell(TestItem.AddressA, new UInt256(index)), new UInt256((ulong)block + 1));
            state.Commit(Prague.Instance, commitRoots: false);
            state.Commit(Prague.Instance);
            state.CommitTree((ulong)block);
            roots[block] = state.StateRoot;
        }
    }

    private static (Hash256 First, Hash256 Second) FlushTwice(bool parallel, bool deferStorageTrieCommit)
    {
        ConfigProvider config = new();
        IFlatDbConfig flat = config.GetConfig<IFlatDbConfig>();
        flat.Enabled = true;
        flat.ParallelStorageRoot = parallel;
        flat.DeferStorageTrieCommit = deferStorageTrieCommit;
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule(config)).Build();
        IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().GlobalWorldState;
        using ILifetimeScope processing = container.BeginLifetimeScope(builder => builder.RegisterInstance(provider).As<IWorldStateScopeProvider>());
        IWorldState state = processing.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(TestItem.AddressA, UInt256.One);
        for (ulong index = 0; index < SlotCount; index++)
            state.Set(new StorageCell(TestItem.AddressA, new UInt256(index)), UInt256.One);
        state.Commit(Frontier.Instance);
        state.RecalculateStateRoot();
        Hash256 first = state.StateRoot;

        state.Set(new StorageCell(TestItem.AddressA, UInt256.Zero), new UInt256(2));
        state.Commit(Frontier.Instance);
        state.RecalculateStateRoot();
        return (first, state.StateRoot);
    }

    private static Hash256 ComputeRoot(bool parallel, bool eagerHash, bool viaPrewarmerScope, bool deferStorageTrieCommit)
    {
        ConfigProvider configProvider = new();
        IFlatDbConfig flatConfig = configProvider.GetConfig<IFlatDbConfig>();
        flatConfig.Enabled = true;
        flatConfig.ParallelStorageRoot = parallel;
        flatConfig.ParallelStorageRootEagerHash = eagerHash;
        flatConfig.DeferStorageTrieCommit = deferStorageTrieCommit;
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule(configProvider)).Build();
        IWorldStateScopeProvider scopeProvider = container.Resolve<IWorldStateManager>().GlobalWorldState;
        if (viaPrewarmerScope)
        {
            // The node's main scope is decorated this way; the wrapper must forward the committed-value hints.
            PrewarmerState mainScopeState = new(new PreBlockCaches(TestPreBlockCachesConfig.Small, PrecompileCaches.Empty), isPrewarmer: false);
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
