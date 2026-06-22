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
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Owns the second, statically witness-wired <see cref="IBlockProcessor"/> graph that the
/// <see cref="WitnessCapturingBlockProcessor"/> selector delegates a witnessed block to. The graph
/// shares the main pipeline's writable <see cref="IWorldState"/> through a transparent
/// <see cref="WitnessGeneratingWorldState"/> recorder, so processing through it is the real block
/// import — recorded as a side effect rather than re-executed.
/// </summary>
/// <remarks>
/// <para>
/// The graph is built off the <em>root</em> lifetime scope (never the main-processing child scope) so
/// it does not inherit that scope's <see cref="IBlockProcessor"/> selector decorator — building off the
/// main scope would make the witness processor resolve back through the selector and form a cycle. The
/// recorder still wraps the exact main-pipeline <see cref="IWorldState"/> instance (taken from
/// <see cref="IMainProcessingContext.WorldState"/>) so the <see cref="Processing.BranchProcessor"/>'s
/// scope/commit on that instance and the witness execution stay coherent.
/// </para>
/// <para>
/// Construction is deferred to the first witnessed block: nodes on an EIP-7928 chain that never receive
/// an <c>engine_newPayloadWithWitness</c> request never pay for a second processing graph. The selector
/// drives blocks serially on the processing loop, so the recorder is reused across blocks — cleared via
/// <see cref="ResetForBlock"/> before each capture.
/// </para>
/// </remarks>
public sealed class WitnessCapturingBlockProcessingEnv(
    ILifetimeScope rootLifetimeScope,
    IWorldStateManager worldStateManager,
    IHeaderStore headerStore,
    IBlockValidationModule[] validationModules) : IDisposable
{
    private readonly Lazy<Built> _built = new(() =>
        Build(rootLifetimeScope, worldStateManager, headerStore, validationModules));

    /// <summary>The witness-wired block processor; the same instance is reused for every witnessed block.</summary>
    public IBlockProcessor Processor => _built.Value.Graph.Processor;

    /// <summary>Clears the recorder accumulators so the next witnessed block starts from a clean slate.</summary>
    public void ResetForBlock()
    {
        Graph graph = _built.Value.Graph;
        graph.Recorder.Reset();
        graph.HeaderRecorder.Reset();
    }

    /// <summary>Projects the accesses recorded during the last <see cref="Processor"/> run into a witness.</summary>
    public Witness GetWitness(BlockHeader parent) => _built.Value.Graph.Recorder.GetWitness(parent);

    public void Dispose()
    {
        if (_built.IsValueCreated) _built.Value.Dispose();
    }

    private static Built Build(
        ILifetimeScope rootLifetimeScope,
        IWorldStateManager worldStateManager,
        IHeaderStore headerStore,
        IBlockValidationModule[] validationModules)
    {
        // The exact main-pipeline world state the BranchProcessor scopes/commits; the recorder wraps it.
        IWorldState parentWorldState = rootLifetimeScope.Resolve<IMainProcessingContext>().WorldState;

        // Read-only trie store for the post-execution witness walk at the parent state root.
        IReadOnlyTrieStore trieStore = worldStateManager.CreateReadOnlyTrieStore();
        WitnessHeaderRecorder headerRecorder = new();
        WitnessGeneratingWorldState recorder = new(
            parentWorldState,
            worldStateManager.GlobalStateReader,
            trieStore,
            headerRecorder,
            headerStore);
        WitnessCapturingHeaderFinder recordingFinder = new(headerStore, headerRecorder);

        ILifetimeScope scope = rootLifetimeScope.BeginLifetimeScope(builder => builder
            // Recorder over the shared writable state — registered by instance, so the decorator wraps
            // the captured parent instance rather than re-resolving itself (no cycle). Also exposed as its
            // concrete type and alongside the header recorder so the Graph bundle below resolves cleanly.
            .AddScoped<IWorldState>(recorder)
            .AddScoped<WitnessGeneratingWorldState>(recorder)
            .AddScoped<WitnessHeaderRecorder>(headerRecorder)
            // Recording header finder + scoped blockhash cache so BLOCKHASH header reads are captured.
            .AddScoped<IHeaderFinder>(recordingFinder)
            .AddScoped<IBlockhashCache, BlockhashCache>()
            // Non-caching code repo so every bytecode/code-hash lookup flows through the recorder.
            .AddScoped<ICodeInfoRepository, CodeInfoRepository>()
            // Witness-mode BAL: statically sequential + non-caching, no parallel parent-reader pool that
            // would read pre-state outside the recorder.
            .AddScoped<IBlockAccessListManager>(ctx => new BlockAccessListManager(
                ctx.Resolve<IWorldState>(),
                ctx.Resolve<ISpecProvider>(),
                ctx.Resolve<IBlockhashProvider>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<IBlocksConfig>(),
                ctx.Resolve<IWithdrawalProcessorFactory>(),
                witnessMode: true))
            // The validation transaction executor; everything else (BlockProcessor, validators, beacon
            // root/blockhash/withdrawal/exec-requests processors, VM, tx processor) is inherited from the
            // root registrations and re-resolved here against the overridden world state.
            .AddModule(validationModules)
            .AddScoped<Graph>());

        return new Built(scope, trieStore, scope.Resolve<Graph>());
    }

    /// <summary>The DI-resolved witness bundle: the block processor plus the recorders the holder reads.</summary>
    private sealed class Graph(
        IBlockProcessor processor,
        WitnessGeneratingWorldState recorder,
        WitnessHeaderRecorder headerRecorder)
    {
        public IBlockProcessor Processor => processor;
        public WitnessGeneratingWorldState Recorder => recorder;
        public WitnessHeaderRecorder HeaderRecorder => headerRecorder;
    }

    /// <summary>
    /// Hand-built owner of the witness scope and the externally-owned witness-walk trie store (the bundle
    /// itself is resolved from the scope). Disposing both here — rather than from a scope-resolved
    /// disposable — avoids re-entering scope disposal, mirroring the witness env factory's pooled entry.
    /// </summary>
    private sealed record Built(ILifetimeScope Scope, IReadOnlyTrieStore TrieStore, Graph Graph) : IDisposable
    {
        public void Dispose()
        {
            Scope.Dispose();
            TrieStore.Dispose();
        }
    }
}
