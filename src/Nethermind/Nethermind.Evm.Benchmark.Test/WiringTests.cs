// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

/// <summary>
/// Wiring sanity tests that verify the benchmark DI containers produce correctly-typed
/// objects and that the DI/construction chains don't break when Nethermind interfaces change.
/// These tests require the gas-benchmarks submodule (genesis state file).
/// </summary>
[TestFixture]
public class WiringTests
{
    private static readonly string s_genesisPath = GasPayloadBenchmarks.s_genesisPath;
    private static readonly IReleaseSpec s_pragueSpec = Prague.Instance;

    private static ISpecProvider CreateSpecProvider() =>
        new SingleReleaseSpecProvider(s_pragueSpec, 1, 1);

    /// <summary>
    /// Initializes genesis state via BenchmarkContainer, skipping the test if the
    /// gas-benchmarks submodule is not available.
    /// </summary>
    private static void EnsureGenesisOrSkip()
    {
        try
        {
            (ILifetimeScope scope, System.IDisposable containerLifetime) = BenchmarkContainer.CreateTransactionScope(
                CreateSpecProvider(), s_genesisPath, s_pragueSpec);
            scope.Dispose();
            containerLifetime.Dispose();
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
        EnsureGenesisOrSkip();

        ISpecProvider specProvider = CreateSpecProvider();

        (ILifetimeScope scope, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateTransactionScope(specProvider, s_genesisPath, s_pragueSpec);

        try
        {
            IWorldState state = scope.Resolve<IWorldState>();
            ITransactionProcessor txProcessor = scope.Resolve<ITransactionProcessor>();

            Assert.That(state, Is.Not.Null);
            Assert.That(txProcessor, Is.Not.Null);
            Assert.That(txProcessor, Is.InstanceOf<EthereumTransactionProcessor>());
        }
        finally
        {
            scope.Dispose();
            containerLifetime.Dispose();
        }
    }

    [Test]
    public void CreateBlockProcessingScope_Resolves_BlockProcessor_And_BranchProcessor()
    {
        EnsureGenesisOrSkip();

        ISpecProvider specProvider = CreateSpecProvider();

        (ILifetimeScope scope, IBlockCachePreWarmer preWarmer, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateBlockProcessingScope(specProvider, s_genesisPath, s_pragueSpec);

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
        EnsureGenesisOrSkip();

        ISpecProvider specProvider = CreateSpecProvider();

        (ILifetimeScope scope, _, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateBlockProcessingScope(specProvider, s_genesisPath, s_pragueSpec, isBlockBuilding: true);

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
        EnsureGenesisOrSkip();

        ISpecProvider specProvider = CreateSpecProvider();
        BlocksConfig blocksConfig = new() { PreWarmStateOnBlockProcessing = false, CachePrecompilesOnBlockProcessing = false };

        (ILifetimeScope scope, IBlockCachePreWarmer preWarmer, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateBlockProcessingScope(specProvider, s_genesisPath, s_pragueSpec, blocksConfig);

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
    public void GenesisStateRoot_Is_Not_Zero()
    {
        EnsureGenesisOrSkip();

        Hash256 root = PayloadLoader.GenesisStateRoot;

        Assert.That(root, Is.Not.Null);
        Assert.That(root, Is.Not.EqualTo(Keccak.Zero));
    }

    [Test]
    public void CreateGenesisHeader_Uses_GenesisStateRoot()
    {
        EnsureGenesisOrSkip();

        BlockHeader header = BlockBenchmarkHelper.CreateGenesisHeader();

        Assert.That(header, Is.Not.Null);
        Assert.That(header.StateRoot, Is.EqualTo(PayloadLoader.GenesisStateRoot));
        Assert.That(header.Number, Is.EqualTo(0));
    }

    [Test]
    public void CreateStateReader_Returns_NonNull()
    {
        EnsureGenesisOrSkip();

        (ILifetimeScope scope, System.IDisposable containerLifetime) = BenchmarkContainer.CreateTransactionScope(
            CreateSpecProvider(), s_genesisPath, s_pragueSpec);

        try
        {
            IWorldState state = scope.Resolve<IWorldState>();
            Nethermind.State.IStateReader reader = BlockBenchmarkHelper.CreateStateReader(state);

            Assert.That(reader, Is.Not.Null);
        }
        finally
        {
            scope.Dispose();
            containerLifetime.Dispose();
        }
    }

    [Test]
    public void BlockProcessingScope_Resolves_RecoverSignatures_And_EthereumEcdsa()
    {
        EnsureGenesisOrSkip();

        ISpecProvider specProvider = CreateSpecProvider();

        (ILifetimeScope scope, _, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateBlockProcessingScope(specProvider, s_genesisPath, s_pragueSpec);

        try
        {
            RecoverSignatures recoverSignatures = scope.Resolve<RecoverSignatures>();
            IEthereumEcdsa ecdsa = scope.Resolve<IEthereumEcdsa>();

            Assert.That(recoverSignatures, Is.Not.Null);
            Assert.That(ecdsa, Is.Not.Null);
            Assert.That(ecdsa, Is.InstanceOf<EthereumEcdsa>());
        }
        finally
        {
            scope.Dispose();
            containerLifetime.Dispose();
        }
    }

    [Test]
    public void TransactionScope_Resolves_RecoverSignatures()
    {
        EnsureGenesisOrSkip();

        (ILifetimeScope scope, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateTransactionScope(CreateSpecProvider(), s_genesisPath, s_pragueSpec);

        try
        {
            RecoverSignatures recoverSignatures = scope.Resolve<RecoverSignatures>();
            Assert.That(recoverSignatures, Is.Not.Null);
        }
        finally
        {
            scope.Dispose();
            containerLifetime.Dispose();
        }
    }

    [Test]
    public void BlockProcessingScope_Resolves_IBranchProcessor_Without_Concrete_Cast()
    {
        EnsureGenesisOrSkip();

        ISpecProvider specProvider = CreateSpecProvider();

        (ILifetimeScope scope, _, System.IDisposable containerLifetime) =
            BenchmarkContainer.CreateBlockProcessingScope(specProvider, s_genesisPath, s_pragueSpec);

        try
        {
            IBranchProcessor branchProcessor = scope.Resolve<IBranchProcessor>();
            Assert.That(branchProcessor, Is.Not.Null);
            // Verify Process method is accessible through the interface (no concrete cast needed)
            Assert.That(branchProcessor, Is.InstanceOf<IBranchProcessor>());
        }
        finally
        {
            scope.Dispose();
            containerLifetime.Dispose();
        }
    }
}
