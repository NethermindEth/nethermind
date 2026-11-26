// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Owns the second, witness-wired <see cref="IBlockProcessor"/> graph that the
/// <see cref="WitnessCapturingBlockProcessor"/> selector delegates a witnessed block to, recording the
/// real block import as a side effect via a <see cref="WitnessGeneratingWorldState"/> recorder.
/// </summary>
/// <remarks>
/// Built as a child of the main-processing scope, so the graph is the main pipeline's own processor chain, decorators
/// included, over a recorder that wraps the exact main-pipeline <see cref="IWorldState"/> instance, which keeps
/// scope/commit and witness execution coherent. The selector built inside the graph hands over the processors it wraps
/// (<see cref="WitnessGraphCore"/>), so a witnessed block runs the decorators below the selector once, on the graph, and
/// those above it once, on the main pipeline. Construction is deferred to the first witnessed block; the recorder is
/// reused across serially-processed blocks, cleared via <see cref="ResetForBlock"/>.
/// </remarks>
public sealed class WitnessCapturingBlockProcessingEnv(
    ILifetimeScope rootLifetimeScope,
    IWorldStateManager worldStateManager,
    IHeaderStore headerStore) : IDisposable
{
    private readonly Lazy<Graph> _graph = new(() => Build(rootLifetimeScope, worldStateManager, headerStore));

    /// <summary>The witness-wired block processor; the same instance is reused for every witnessed block.</summary>
    public IBlockProcessor Processor => _graph.Value.Processor;

    /// <summary>Clears the recorder accumulators so the next witnessed block starts from a clean slate.</summary>
    public void ResetForBlock()
    {
        Graph graph = _graph.Value;
        graph.Recorder.Reset();
        graph.HeaderRecorder.Reset();
        graph.BlockhashCache.Clear();
    }

    /// <summary>Projects the accesses recorded during the last <see cref="Processor"/> run into a witness.</summary>
    public Witness GetWitness(BlockHeader parent) => _graph.Value.Recorder.GetWitness(parent);

    public void Dispose()
    {
        if (_graph.IsValueCreated) _graph.Value.Dispose();
    }

    private static Graph Build(ILifetimeScope rootLifetimeScope, IWorldStateManager worldStateManager, IHeaderStore headerStore)
    {
        IMainProcessingContext mainProcessing = rootLifetimeScope.Resolve<IMainProcessingContext>();

        IReadOnlyTrieStore trieStore = worldStateManager.CreateReadOnlyTrieStore();
        WitnessHeaderRecorder headerRecorder = new();
        WitnessGeneratingWorldState recorder = new(
            mainProcessing.WorldState,
            worldStateManager.GlobalStateReader,
            trieStore,
            headerRecorder,
            headerStore);
        WitnessCapturingHeaderFinder recordingFinder = new(headerStore, headerRecorder);
        WitnessGraphCore core = new();

        ILifetimeScope scope = mainProcessing.LifetimeScope.BeginLifetimeScope(builder => builder
            // Registered by instance, so the decorator wraps the captured parent instance rather than re-resolving itself (no cycle).
            .AddScoped<IWorldState>(recorder)
            .AddScoped<IHeaderFinder>(recordingFinder)
            .AddScoped<IBlockhashCache, BlockhashCache>()
            .AddScoped<ICodeCache>(NoopCodeCache.Instance)
            .AddScoped<IBlockAccessListManager>(ctx => new BlockAccessListManager(
                ctx.Resolve<IWorldState>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<IBlocksConfig>(),
                ctx.Resolve<IWithdrawalProcessorFactory>(),
                ctx.Resolve<BalTxProcessorFactory>(),
                zeroNonceStorageAccountsTransition: ctx.Resolve<ZeroNonceStorageAccountsTransition>()))
            .AddScoped(core));

        // Resolving the pipeline builds its selector, which hands the chain below it to the core.
        scope.Resolve<IBlockProcessor>();
        IBlockProcessor processor = core.Processor
            ?? throw new InvalidOperationException($"{nameof(WitnessCapturingBlockProcessor)} is not in the main processing pipeline");
        IBlockhashCache blockhashCache = scope.Resolve<IBlockhashCache>();
        return new Graph(scope, trieStore, recorder, headerRecorder, processor, blockhashCache);
    }

    /// <summary>The witness bundle plus the scope and witness-walk trie store whose lifetime it owns.</summary>
    private sealed class Graph(
        ILifetimeScope scope,
        IReadOnlyTrieStore trieStore,
        WitnessGeneratingWorldState recorder,
        WitnessHeaderRecorder headerRecorder,
        IBlockProcessor processor,
        IBlockhashCache blockhashCache) : IDisposable
    {
        public WitnessGeneratingWorldState Recorder => recorder;
        public WitnessHeaderRecorder HeaderRecorder => headerRecorder;
        public IBlockProcessor Processor => processor;
        public IBlockhashCache BlockhashCache => blockhashCache;

        public void Dispose()
        {
            try { scope.Dispose(); }
            finally { trieStore.Dispose(); }
        }
    }
}

/// <summary>Holds what the <see cref="WitnessCapturingBlockProcessor"/> built inside the witness graph wraps: the decorators
/// below the selector over the witness-wired core, which the graph runs a witnessed block on.</summary>
public sealed class WitnessGraphCore
{
    public IBlockProcessor? Processor { get; set; }
}
