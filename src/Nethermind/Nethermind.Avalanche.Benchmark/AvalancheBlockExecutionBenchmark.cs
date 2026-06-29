// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Avalanche.Benchmark;

/// <summary>
/// Block-execution throughput benchmark for Avalanche C-Chain blocks. Reuses Nethermind's production
/// <see cref="BranchProcessor"/> pipeline — wired through the test DI modules with the Avalanche
/// <see cref="ISpecProvider"/> overriding <c>ISpecProvider</c> — to execute a contiguous range of real
/// blocks and measure per-block wall-clock execution time.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the canonical wiring of <c>Nethermind.Evm.Benchmark.BlockProcessingBenchmark</c>: a single
/// DI container plus a child processing scope holding the world state, then
/// <see cref="IBranchProcessor.Process"/> with <see cref="ProcessingOptions.NoValidation"/>. Each block
/// is processed in its own <c>Process</c> call, anchored at the previous processed block's state root,
/// so per-block wall-clock can be captured. <c>NoValidation</c> skips header/state-root validation,
/// keeping the measurement focused on EVM transaction execution and state updates rather than on
/// consensus checks that would require the full canonical state.
/// </para>
/// <para>
/// Limitations (see README): blocks must be seeded with a pre-state covering the accounts/storage their
/// transactions read, otherwise missing-trie-node errors surface as failed blocks (reported, not fatal).
/// The harness measures EVM execution throughput, not Avalanche-specific atomic/extData processing.
/// </para>
/// </remarks>
public sealed class AvalancheBlockExecutionBenchmark : IDisposable
{
    private readonly IContainer _container;
    private readonly ILifetimeScope _processingScope;
    private readonly IBranchProcessor _branchProcessor;

    /// <summary>The state root the first benchmarked block is processed against.</summary>
    public Hash256 SeededStateRoot { get; }

    /// <summary>Number of accounts seeded from the pre-state (zero if none was provided).</summary>
    public int SeededAccountCount { get; }

    /// <summary>
    /// Builds the processing environment: DI container with the Avalanche spec provider, an in-memory
    /// world state, and (optionally) a seeded pre-state.
    /// </summary>
    /// <param name="specProvider">The Avalanche C-Chain spec provider (chain id 43114).</param>
    /// <param name="preStatePath">Optional path to a JSON pre-state to seed; null/empty for an empty state.</param>
    public AvalancheBlockExecutionBenchmark(ISpecProvider specProvider, string? preStatePath)
    {
        // Production block-processing modules wired by TestNethermindModule (PseudoNethermindModule +
        // TestEnvironmentModule with in-memory DBs and the prewarmer). The Avalanche spec provider is
        // registered last so it overrides the default ISpecProvider for the whole container.
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ISpecProvider>(specProvider)
            .Build();

        IDbProvider dbProvider = TestMemDbProvider.Init();
        IWorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;

        IBlockValidationModule[] validationModules = _container.Resolve<IBlockValidationModule[]>();
        IMainProcessingModule[] mainProcessingModules = _container.Resolve<IMainProcessingModule[]>();
        _processingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.RegisterInstance(specProvider).As<ISpecProvider>().ExternallyOwned();
            b.AddModule(validationModules);
            b.AddModule(mainProcessingModules);
        });

        IWorldState worldState = _processingScope.Resolve<IWorldState>();

        IReleaseSpec genesisSpec = specProvider.GenesisSpec;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            if (!string.IsNullOrWhiteSpace(preStatePath))
            {
                SeededAccountCount = PreStateLoader.Apply(worldState, preStatePath!, genesisSpec);
            }

            worldState.Commit(genesisSpec);
            worldState.CommitTree(0);
            SeededStateRoot = worldState.StateRoot;
        }

        _branchProcessor = _processingScope.Resolve<IBranchProcessor>();
    }

    /// <summary>
    /// Executes each block once, in order, measuring per-block wall-clock execution time. Each block is
    /// anchored at the previous processed block's resulting state root (the seeded root for the first).
    /// </summary>
    /// <param name="blocks">Contiguous, ascending-by-number blocks to execute.</param>
    /// <returns>The aggregated execution results.</returns>
    public BenchmarkResult Run(IReadOnlyList<Block> blocks)
    {
        List<BlockResult> results = new(blocks.Count);

        // The base header for the first block must expose the seeded state root so BeginScope anchors
        // the world state correctly. Subsequent blocks anchor at the prior processed block's header.
        ulong firstNumber = blocks[0].Number;
        BlockHeader baseHeader = Build.A.BlockHeader
            .WithNumber(firstNumber == 0 ? 0UL : firstNumber - 1)
            .WithStateRoot(SeededStateRoot)
            .WithGasLimit(blocks[0].GasLimit)
            .WithTimestamp(blocks[0].Timestamp == 0 ? 0UL : blocks[0].Timestamp - 1)
            .TestObject;

        foreach (Block block in blocks)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                Block[] processed = _branchProcessor.Process(
                    baseHeader,
                    [block],
                    ProcessingOptions.NoValidation,
                    NullBlockTracer.Instance);

                double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Block processedBlock = processed[0];
                results.Add(new BlockResult(block.Number, block.GasUsed, block.Transactions.Length, elapsedMs, true));

                // Chain to the next block: anchor at the just-produced state.
                baseHeader = processedBlock.Header;
            }
            catch (Exception ex)
            {
                double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                results.Add(new BlockResult(block.Number, block.GasUsed, block.Transactions.Length, elapsedMs, false, ex.GetType().Name + ": " + ex.Message));
                // Re-anchor on the suggested header so a single failed block doesn't cascade — its
                // claimed state root lets later blocks attempt to proceed from the on-disk state.
                baseHeader = block.Header;
            }
        }

        return new BenchmarkResult(results);
    }

    public void Dispose()
    {
        _processingScope.Dispose();
        _container.Dispose();
    }
}
