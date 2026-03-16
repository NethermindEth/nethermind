// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Healing;
using Nethermind.Trie;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Central factory for creating DI containers that wire block processing and world state
/// components via <see cref="PseudoNethermindModule"/> + <see cref="TestEnvironmentModule"/>,
/// matching the production Nethermind pipeline with in-memory storage overrides.
/// Benchmark-specific overrides (genesis state, stub IBlockTree, disabled validation) are
/// layered on top via <see cref="BenchmarkOverrideModule"/>.
/// </summary>
internal static class BenchmarkContainer
{
    /// <summary>
    /// Creates a scope with IWorldState + ITransactionProcessor for tx-level benchmarks (EVMExecute).
    /// Dispose the returned scope AND ContainerLifetime in GlobalCleanup.
    /// </summary>
    public static (ILifetimeScope Scope, IDisposable ContainerLifetime) CreateTransactionScope(
        ISpecProvider specProvider,
        string genesisPath,
        IReleaseSpec genesisSpec,
        IBlocksConfig blocksConfig = null)
    {
        blocksConfig ??= BlockBenchmarkHelper.CreateBenchmarkBlocksConfig();

        IContainer container = BuildContainer(specProvider, blocksConfig);
        InitializeGenesisState(container, genesisPath, genesisSpec);

        IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();

        ILifetimeScope scope = container.BeginLifetimeScope(childBuilder =>
        {
            childBuilder
                .AddSingleton<IWorldStateScopeProvider>(worldStateManager.GlobalWorldState)
                .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
                .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>();
        });

        return (scope, container);
    }

    /// <summary>
    /// Creates a scope with full block processing components for block-level benchmarks
    /// (BlockBuilding, NewPayload, NewPayloadMeasured).
    /// Returns the scope, optional pre-warmer, and the root container (dispose in GlobalCleanup).
    /// </summary>
    public static (ILifetimeScope Scope, IBlockCachePreWarmer PreWarmer, IDisposable ContainerLifetime)
        CreateBlockProcessingScope(
            ISpecProvider specProvider,
            string genesisPath,
            IReleaseSpec genesisSpec,
            IBlocksConfig blocksConfig = null,
            bool isBlockBuilding = false,
            Action<ContainerBuilder> additionalRegistrations = null)
    {
        blocksConfig ??= BlockBenchmarkHelper.CreateBenchmarkBlocksConfig();

        IContainer container = BuildContainer(specProvider, blocksConfig);
        InitializeGenesisState(container, genesisPath, genesisSpec);

        IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();

        ILifetimeScope scope = container.BeginLifetimeScope(childBuilder =>
        {
            // Register IWorldStateScopeProvider from production IWorldStateManager
            childBuilder.AddSingleton<IWorldStateScopeProvider>(worldStateManager.GlobalWorldState);

            // Apply prewarmer decorators if prewarming is enabled
            if (blocksConfig.PreWarmStateOnBlockProcessing)
            {
                childBuilder.AddModule(new PrewarmerModule.PrewarmerMainProcessingModule());
            }

            // Register the appropriate tx executor based on mode
            if (isBlockBuilding)
            {
                childBuilder
                    .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
                    .AddScoped<ITransactionProcessorAdapter, BuildUpTransactionProcessorAdapter>();
            }
            else
            {
                childBuilder
                    .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
                    .AddScoped<ITransactionProcessorAdapter, ExecuteTransactionProcessorAdapter>();
            }

            additionalRegistrations?.Invoke(childBuilder);
        });

        IBlockCachePreWarmer preWarmer = blocksConfig.PreWarmStateOnBlockProcessing
            ? scope.Resolve<IBlockCachePreWarmer>()
            : null;

        return (scope, preWarmer, container);
    }

    /// <summary>
    /// Builds the root DI container using production modules via <see cref="PseudoNethermindModule"/>
    /// with <see cref="TestEnvironmentModule"/> for MemDb + test infrastructure, then applies
    /// benchmark-specific overrides.
    /// </summary>
    private static IContainer BuildContainer(ISpecProvider specProvider, IBlocksConfig blocksConfig)
    {
        InitConfig initConfig = new() { DiagnosticMode = DiagnosticMode.MemDb };
        ChainSpec chainSpec = new() { ChainId = specProvider.ChainId };

        IConfigProvider configProvider = new ConfigProvider(
            initConfig,
            blocksConfig,
            new SyncConfig(),
            new ReceiptConfig(),
            new PruningConfig(),
            new DbConfig()
        );

        ContainerBuilder builder = new();
        builder
            .AddModule(new PseudoNethermindModule(chainSpec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, null))
            .AddModule(new BenchmarkOverrideModule(specProvider));

        return builder.Build();
    }

    /// <summary>
    /// Initializes the genesis state from the chainspec file into the DI-provided world state.
    /// </summary>
    private static void InitializeGenesisState(IContainer container, string genesisPath, IReleaseSpec spec)
    {
        IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();
        WorldState genesisState = new(worldStateManager.GlobalWorldState, LimboLogs.Instance);
        PayloadLoader.InitializeGenesis(genesisState, genesisPath, spec);
    }

    /// <summary>
    /// Benchmark-specific overrides on top of the production + test modules.
    /// Disables validation, stubs IBlockTree, and overrides ISpecProvider with the benchmark-provided one.
    /// </summary>
    private sealed class BenchmarkOverrideModule(ISpecProvider specProvider) : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder
                // Override ISpecProvider — benchmarks use a specific SingleReleaseSpecProvider, not ChainSpecBased
                .AddSingleton(specProvider)
                // Override ILogManager — use LimboLogs (faster than TestLogManager for benchmarks)
                .AddSingleton<ILogManager>(LimboLogs.Instance)
                .AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance)
                // RecoverSignatures — resolved from DI for NewPayload and BlockBuilding modes
                .AddSingleton<RecoverSignatures>()
                // Skip block/header validation — benchmark payloads are known-good
                .AddSingleton<IBlockValidator>(Always.Valid)
                // Stub IHeaderFinder — BlockhashCache needs it; benchmarks don't look up historical headers
                .AddSingleton<IHeaderFinder>(new NullHeaderFinder())
                // Stub IBlockTree — benchmarks process blocks with no uncles
                .AddSingleton<IBlockTree>(new MinimalBenchmarkBlockTree())
                .Bind<IBlockFinder, IBlockTree>()
                // Benchmarks have complete state; provide noop recovery
                .AddSingleton<Lazy<IPathRecovery>>(_ => new Lazy<IPathRecovery>(() => new NoopPathRecovery()))
                ;
        }
    }

    private sealed class NoopPathRecovery : IPathRecovery
    {
        public Task<IOwnedReadOnlyList<(TreePath, byte[])>> Recover(Hash256 rootHash, Hash256 address, TreePath startingPath, Hash256 startingNodeHash, Hash256 fullPath, CancellationToken cancellationToken = default)
            => Task.FromResult<IOwnedReadOnlyList<(TreePath, byte[])>>(null);
    }

    private sealed class NullHeaderFinder : IHeaderFinder
    {
        public BlockHeader Get(Hash256 blockHash, long? blockNumber = null) => null;
    }

    /// <summary>
    /// Minimal IBlockTree stub for DI resolution of validators.
    /// All lookups return null/empty via base class defaults.
    /// </summary>
    private sealed class MinimalBenchmarkBlockTree : BenchmarkBlockTreeBase { }
}
