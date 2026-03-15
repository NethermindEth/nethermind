// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using Testably.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Crypto;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Healing;
using Nethermind.Trie;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Central factory for creating DI containers that wire block processing and world state
/// components via production modules (<see cref="BlockProcessingModule"/>, <see cref="WorldStateModule"/>),
/// matching the real Nethermind pipeline. Benchmark-specific overrides (genesis state, stub IBlockTree)
/// are layered on top.
///
/// We intentionally use individual production modules (DbModule, WorldStateModule, PrewarmerModule,
/// BlockProcessingModule) rather than <see cref="NethermindModule"/>. NethermindModule bundles
/// NetworkModule, DiscoveryModule, RpcModules, EraModule, MonitoringModule, and BlockTreeModule —
/// none of which benchmarks need — and requires IConfigProvider + INethermindApi infrastructure.
/// This granular approach keeps benchmarks fast to initialize while still sharing production
/// block processing wiring, so changes to those modules are automatically reflected here.
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
        InitConfig initConfig = new() { DiagnosticMode = DiagnosticMode.MemDb };

        ContainerBuilder builder = new();
        RegisterProductionModules(builder, initConfig, blocksConfig, specProvider);
        RegisterBenchmarkOverrides(builder, specProvider, blocksConfig);

        IContainer container = builder.Build();
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
        InitConfig initConfig = new() { DiagnosticMode = DiagnosticMode.MemDb };

        ContainerBuilder builder = new();
        RegisterProductionModules(builder, initConfig, blocksConfig, specProvider);
        RegisterBenchmarkOverrides(builder, specProvider, blocksConfig);

        IContainer container = builder.Build();
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
    /// Registers production DI modules for world state and block processing.
    /// Uses MemDb via DiagnosticMode.MemDb for in-memory storage.
    /// </summary>
    private static void RegisterProductionModules(
        ContainerBuilder builder,
        InitConfig initConfig,
        IBlocksConfig blocksConfig,
        ISpecProvider specProvider)
    {
        SyncConfig syncConfig = new();
        ReceiptConfig receiptConfig = new();

        builder
            // Production modules — world state and block processing through real DI chain
            .AddModule(new DbModule(initConfig, receiptConfig, syncConfig))
            .AddModule(new WorldStateModule(initConfig))
            .AddModule(new PrewarmerModule(blocksConfig))
            .AddModule(new BlockProcessingModule(initConfig, blocksConfig))

            // Configs required by WorldStateModule and its dependencies
            .AddSingleton<IInitConfig>(initConfig)
            .AddSingleton<ISyncConfig>(syncConfig)
            .AddSingleton<IPruningConfig>(new PruningConfig())
            .AddSingleton<IDbConfig>(new DbConfig())
            .AddSingleton<ILogIndexConfig>(new LogIndexConfig())
            .AddSingleton<IHardwareInfo>(new TestHardwareInfo(1L.GiB))
            .AddSingleton<ITimerFactory>(_ => TimerFactory.Default)
            .AddSingleton<IFileSystem>(_ => new RealFileSystem())
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(default))
            .AddSingleton<ChainSpec>(_ => new ChainSpec { ChainId = specProvider.ChainId })

            // IDisposableStack for PruningTrieStateFactory to register disposables
            .Add<IDisposableStack, AutofacDisposableStack>()

            // Lazy<IPathRecovery> — benchmarks have complete state; provide noop recovery
            .AddSingleton<Lazy<IPathRecovery>>(_ => new Lazy<IPathRecovery>(() => new NoopPathRecovery()))
            ;
    }

    /// <summary>
    /// Initializes the genesis state from the chainspec file into the DI-provided world state.
    /// Thread-safe via PayloadLoader.InitializeGenesis.
    /// </summary>
    private static void InitializeGenesisState(IContainer container, string genesisPath, IReleaseSpec spec)
    {
        IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();
        WorldState genesisState = new(worldStateManager.GlobalWorldState, LimboLogs.Instance);
        PayloadLoader.InitializeGenesis(genesisState, genesisPath, spec);
    }

    private static void RegisterBenchmarkOverrides(
        ContainerBuilder builder,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig)
    {
        builder
            .AddSingleton(specProvider)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton(blocksConfig)
            .AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance)
            // IEthereumEcdsa — matches NethermindModule registration, used for RecoverSignatures.
            .AddSingleton<IEthereumEcdsa>(new EthereumEcdsa(specProvider.ChainId))
            // RecoverSignatures — production uses this in BlockchainProcessor; benchmarks
            // resolve it from DI for NewPayload and BlockBuilding modes.
            .AddSingleton<RecoverSignatures>()
            // Skip block/header validation — benchmark payloads are known-good and the
            // MinimalBenchmarkBlockTree cannot satisfy parent-header lookups.
            .AddSingleton<IBlockValidator>(Always.Valid)
            // Stub IHeaderFinder — BlockhashCache needs it; benchmarks don't look up historical headers.
            .AddSingleton<IHeaderFinder>(new NullHeaderFinder())
            // Stub IBlockTree — only needed for UnclesValidator resolution + BlockchainProcessor.
            // Benchmarks process blocks with no uncles.
            .AddSingleton<IBlockTree>(new MinimalBenchmarkBlockTree())
            .Bind<IBlockFinder, IBlockTree>()
            ;
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
