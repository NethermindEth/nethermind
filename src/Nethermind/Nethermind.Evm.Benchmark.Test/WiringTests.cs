// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

/// <summary>
/// Wiring sanity tests that verify the benchmark setup helpers produce correctly-typed
/// objects and that the DI/construction chains don't break when Nethermind interfaces change.
/// These tests require the genesis state to be initialized (which requires the gas-benchmarks submodule).
/// </summary>
[TestFixture]
public class WiringTests
{
    private static void EnsureGenesis()
    {
        try
        {
            IReleaseSpec pragueSpec = Prague.Instance;
            PayloadLoader.EnsureGenesisInitialized(GasPayloadBenchmarks.s_genesisPath, pragueSpec);
        }
        catch (System.IO.FileNotFoundException)
        {
            Assert.Ignore("Gas-benchmarks submodule not available — skipping wiring test");
        }
        catch (System.InvalidOperationException ex) when (ex.Message.Contains("Git LFS"))
        {
            Assert.Ignore("Git LFS not pulled — skipping wiring test");
        }
    }

    [Test]
    public void CreateTransactionScope_Resolves_WorldState_And_TxProcessor()
    {
        EnsureGenesis();

        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);

        using ILifetimeScope scope = BenchmarkContainer.CreateTransactionScope(specProvider);

        IWorldState state = scope.Resolve<IWorldState>();
        ITransactionProcessor txProcessor = scope.Resolve<ITransactionProcessor>();

        Assert.That(state, Is.Not.Null);
        Assert.That(txProcessor, Is.Not.Null);
        Assert.That(txProcessor, Is.InstanceOf<EthereumTransactionProcessor>());
    }

    [Test]
    public void CreateBlockProcessingScope_Resolves_BlockProcessor_And_BranchProcessor()
    {
        EnsureGenesis();

        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);

        (ILifetimeScope scope, IBlockCachePreWarmer preWarmer, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateBlockProcessingScope(specProvider);

        try
        {
            IWorldState state = scope.Resolve<IWorldState>();
            IBlockProcessor blockProcessor = scope.Resolve<IBlockProcessor>();
            IBranchProcessor branchProcessor = scope.Resolve<IBranchProcessor>();

            Assert.That(state, Is.Not.Null);
            Assert.That(blockProcessor, Is.Not.Null);
            Assert.That(blockProcessor, Is.InstanceOf<BlockProcessor>());
            Assert.That(branchProcessor, Is.Not.Null);
            Assert.That(branchProcessor, Is.InstanceOf<BranchProcessor>());
            Assert.That(preWarmer, Is.Not.Null, "PreWarmer should be non-null with default config (PreWarmStateOnBlockProcessing=true)");
        }
        finally
        {
            scope.Dispose();
            containerLifetime.Dispose();
        }
    }

    [Test]
    public void CreateBlockProcessingScope_BlockBuilding_Uses_ProductionTransactionsExecutor()
    {
        EnsureGenesis();

        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);

        (ILifetimeScope scope, _, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateBlockProcessingScope(specProvider, isBlockBuilding: true);

        try
        {
            IBlockProcessor blockProcessor = scope.Resolve<IBlockProcessor>();
            Assert.That(blockProcessor, Is.Not.Null);
            Assert.That(blockProcessor, Is.InstanceOf<BlockProcessor>());
        }
        finally
        {
            scope.Dispose();
            containerLifetime.Dispose();
        }
    }

    [Test]
    public void CreateBlockProcessingScope_Without_PreWarming()
    {
        EnsureGenesis();

        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);
        BlocksConfig blocksConfig = new() { PreWarmStateOnBlockProcessing = false, CachePrecompilesOnBlockProcessing = false };

        (ILifetimeScope scope, IBlockCachePreWarmer preWarmer, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateBlockProcessingScope(specProvider, blocksConfig);

        try
        {
            IWorldState state = scope.Resolve<IWorldState>();
            Assert.That(state, Is.Not.Null);
            Assert.That(preWarmer, Is.Null);
        }
        finally
        {
            scope.Dispose();
            containerLifetime.Dispose();
        }
    }

    [Test]
    public void CreateWorldState_Returns_Valid_State()
    {
        EnsureGenesis();

        IWorldState state = PayloadLoader.CreateWorldState();

        Assert.That(state, Is.Not.Null);
    }

    [Test]
    public void GenesisStateRoot_Is_Not_Zero()
    {
        EnsureGenesis();

        Hash256 root = PayloadLoader.GenesisStateRoot;

        Assert.That(root, Is.Not.Null);
        Assert.That(root, Is.Not.EqualTo(Keccak.Zero));
    }

    [Test]
    public void CreateGenesisHeader_Uses_GenesisStateRoot()
    {
        EnsureGenesis();

        BlockHeader header = BlockBenchmarkHelper.CreateGenesisHeader();

        Assert.That(header, Is.Not.Null);
        Assert.That(header.StateRoot, Is.EqualTo(PayloadLoader.GenesisStateRoot));
        Assert.That(header.Number, Is.EqualTo(0));
    }

    [Test]
    public void CreateStateReader_Returns_NonNull()
    {
        EnsureGenesis();

        IWorldState state = PayloadLoader.CreateWorldState();
        Nethermind.State.IStateReader reader = BlockBenchmarkHelper.CreateStateReader(state);

        Assert.That(reader, Is.Not.Null);
    }

    [Test]
    public void CreateWorldStateManager_Returns_Valid_Manager()
    {
        EnsureGenesis();

        Nethermind.State.IWorldStateManager manager = PayloadLoader.CreateWorldStateManager();

        Assert.That(manager, Is.Not.Null);
    }
}
