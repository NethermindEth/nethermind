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
