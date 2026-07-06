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
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
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
/// Built off the <em>root</em> lifetime scope (not the main-processing child scope) to avoid inheriting
/// the selector decorator and forming a cycle, while the recorder still wraps the exact main-pipeline
/// <see cref="IWorldState"/> instance so scope/commit and witness execution stay coherent. Construction
/// is deferred to the first witnessed block; the recorder is reused across serially-processed blocks,
/// cleared via <see cref="ResetForBlock"/>.
/// </remarks>
public sealed class WitnessCapturingBlockProcessingEnv(
    ILifetimeScope rootLifetimeScope,
    IProcessingEnvBuilder envBuilder,
    IWorldStateManager worldStateManager,
    IHeaderStore headerStore) : IDisposable
{
    private readonly Lazy<IGraph> _graph = new(() =>
        Build(rootLifetimeScope, envBuilder, worldStateManager, headerStore));

    /// <summary>The witness-wired block processor; the same instance is reused for every witnessed block.</summary>
    public IBlockProcessor Processor => _graph.Value.Processor;

    /// <summary>Clears the recorder accumulators so the next witnessed block starts from a clean slate.</summary>
    public void ResetForBlock()
    {
        IGraph graph = _graph.Value;
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

    private static IGraph Build(
        ILifetimeScope rootLifetimeScope,
        IProcessingEnvBuilder envBuilder,
        IWorldStateManager worldStateManager,
        IHeaderStore headerStore)
    {
        IWorldState parentWorldState = rootLifetimeScope.Resolve<IMainProcessingContext>().WorldState;

        IReadOnlyTrieStore trieStore = worldStateManager.CreateReadOnlyTrieStore();
        WitnessHeaderRecorder headerRecorder = new();
        WitnessGeneratingWorldState recorder = new(
            parentWorldState,
            worldStateManager.GlobalStateReader,
            trieStore,
            headerRecorder,
            headerStore);
        WitnessCapturingHeaderFinder recordingFinder = new(headerStore, headerRecorder);

        return envBuilder
            .WithWorldState(recorder)
            .WithComponent(recorder)
            .WithComponent(headerRecorder)
            .ThatDisposes(trieStore)
            .WithReplacedComponent<IHeaderFinder>(recordingFinder)
            .WithReplacedComponent<IBlockhashCache, BlockhashCache>()
            .WithReplacedComponent<ICodeInfoRepository, CodeInfoRepository>()
            .WithReplacedComponent<IBlockAccessListManager>(ctx => new BlockAccessListManager(
                ctx.Resolve<IWorldState>(),
                ctx.Resolve<ISpecProvider>(),
                ctx.Resolve<IBlockhashProvider>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<IBlocksConfig>(),
                ctx.Resolve<IWithdrawalProcessorFactory>(),
                codeInfoRepositoryFactory: CodeInfoRepositoryFactories.Witness,
                transactionProcessorFactory: ctx.Resolve<ITransactionProcessorFactory>()))
            .WithBlockValidationConfiguration()
            .BuildAs<IGraph>();
    }

    /// <summary>
    /// The resolved witness components (plus the scope-owned witness-walk trie store); disposing it
    /// disposes the underlying child scope and everything it owns.
    /// </summary>
    public interface IGraph : IDisposable
    {
        IBlockProcessor Processor { get; }
        IBlockhashCache BlockhashCache { get; }
        WitnessGeneratingWorldState Recorder { get; }
        WitnessHeaderRecorder HeaderRecorder { get; }
    }
}
