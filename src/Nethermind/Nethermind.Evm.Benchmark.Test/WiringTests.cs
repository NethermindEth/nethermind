// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
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
    private static bool GenesisAvailable()
    {
        try
        {
            _ = PayloadLoader.GenesisStateRoot;
            return true;
        }
        catch
        {
            return false;
        }
    }

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
    public void CreateTransactionProcessor_Returns_Valid_Processor()
    {
        EnsureGenesis();

        IWorldState state = PayloadLoader.CreateWorldState();
        TestBlockhashProvider blockhashProvider = new();
        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);

        ITransactionProcessor txProcessor = BlockBenchmarkHelper.CreateTransactionProcessor(
            state, blockhashProvider, specProvider);

        Assert.That(txProcessor, Is.Not.Null);
        Assert.That(txProcessor, Is.InstanceOf<EthereumTransactionProcessor>());
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
    public void CreateBlockProcessor_Returns_Valid_Processor()
    {
        EnsureGenesis();

        IWorldState state = PayloadLoader.CreateWorldState();
        TestBlockhashProvider blockhashProvider = new();
        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);
        ITransactionProcessor txProcessor = BlockBenchmarkHelper.CreateTransactionProcessor(
            state, blockhashProvider, specProvider);

        Nethermind.Consensus.Processing.BlockProcessor blockProcessor =
            BlockBenchmarkHelper.CreateBlockProcessor(specProvider, txProcessor, state);

        Assert.That(blockProcessor, Is.Not.Null);
    }

    [Test]
    public void CreateBlockBuildingProcessor_Returns_Valid_Processor()
    {
        EnsureGenesis();

        IWorldState state = PayloadLoader.CreateWorldState();
        TestBlockhashProvider blockhashProvider = new();
        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);
        ITransactionProcessor txProcessor = BlockBenchmarkHelper.CreateTransactionProcessor(
            state, blockhashProvider, specProvider);

        Nethermind.Consensus.Processing.BlockProcessor blockProcessor =
            BlockBenchmarkHelper.CreateBlockBuildingProcessor(specProvider, txProcessor, state);

        Assert.That(blockProcessor, Is.Not.Null);
    }

    [Test]
    public void CreateBranchProcessingContext_Without_PreWarming()
    {
        EnsureGenesis();

        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);
        TestBlockhashProvider blockhashProvider = new();
        BlocksConfig blocksConfig = new() { PreWarmStateOnBlockProcessing = false, CachePrecompilesOnBlockProcessing = false };

        BlockBenchmarkHelper.BranchProcessingContext context =
            BlockBenchmarkHelper.CreateBranchProcessingContext(specProvider, blockhashProvider, blocksConfig);

        Assert.That(context.State, Is.Not.Null);
        Assert.That(context.PreWarmer, Is.Null);
        Assert.That(context.PreWarmerLifetime, Is.Null);
        Assert.That(context.PreBlockCaches, Is.Null);
        Assert.That(context.CachePrecompiles, Is.False);
    }

    [Test]
    public void CreateBranchProcessor_Returns_Valid_Processor()
    {
        EnsureGenesis();

        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);
        TestBlockhashProvider blockhashProvider = new();
        IWorldState state = PayloadLoader.CreateWorldState();
        ITransactionProcessor txProcessor = BlockBenchmarkHelper.CreateTransactionProcessor(
            state, blockhashProvider, specProvider);

        Nethermind.Consensus.Processing.BlockProcessor blockProcessor =
            BlockBenchmarkHelper.CreateBlockProcessor(specProvider, txProcessor, state);

        Nethermind.Consensus.Processing.BranchProcessor branchProcessor =
            BlockBenchmarkHelper.CreateBranchProcessor(blockProcessor, specProvider, state, txProcessor, blockhashProvider, null);

        Assert.That(branchProcessor, Is.Not.Null);
    }

    [Test]
    public void CreateTransactionProcessingContext_Returns_All_Components()
    {
        EnsureGenesis();

        ISpecProvider specProvider = new SingleReleaseSpecProvider(Prague.Instance, 1, 1);

        BlockBenchmarkHelper.TransactionProcessingContext context =
            BlockBenchmarkHelper.CreateTransactionProcessingContext(specProvider, Prague.Instance);

        Assert.That(context.State, Is.Not.Null);
        Assert.That(context.StateScope, Is.Not.Null);
        Assert.That(context.TransactionProcessor, Is.Not.Null);
        Assert.That(context.TransactionProcessor, Is.InstanceOf<EthereumTransactionProcessor>());

        context.StateScope.Dispose();
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
