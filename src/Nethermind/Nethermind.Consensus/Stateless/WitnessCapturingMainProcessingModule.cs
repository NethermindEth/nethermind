// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// On EIP-7928 chains, wires up in-flight witness capture for the main processing pipeline:
/// installs the <see cref="WitnessCapturingWorldStateProxy"/>, <see cref="WitnessCapturingHeaderFinder"/>
/// and <see cref="WitnessCapturingBlockProcessor"/> decorators, and the shared
/// <see cref="WitnessCaptureSession"/> that they all consult for the active per-block recorders.
/// </summary>
public sealed class WitnessCapturingMainProcessingModule(ISpecProvider specProvider) : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder)
    {
        if (!specProvider.GetFinalSpec().IsEip7928Enabled) return;

        // Note: WitnessCaptureSession is registered at root (by the merge plugin) so the main-world
        // trie store's read-tap — constructed at root, before this child scope exists — shares the
        // same instance the decorators below consult. Re-registering it here would shadow it.

        // Signals to this scope's BlockAccessListManager that it executes for witness capture; the
        // predicate tracks the armed session, so BAL forces sequential + non-caching only for the
        // block actually being witnessed.
        builder.AddScoped<WitnessExecutionPredicate, WitnessCaptureSession>(
            session => new WitnessExecutionPredicate(() => session.IsActive));

        builder.AddDecorator<IWorldState, WitnessCapturingWorldStateProxy>();
        // Expose the same proxy instance as a typed singleton so the block-processor decorator can
        // take it directly. Cast through IWorldState because Autofac doesn't model decorator chains
        // as typed singletons.
        builder.AddSingleton<WitnessCapturingWorldStateProxy>(ctx =>
            (WitnessCapturingWorldStateProxy)ctx.Resolve<IWorldState>());

        builder.AddDecorator<IHeaderFinder, WitnessCapturingHeaderFinder>();
        // Same typed-singleton bridge for the header-finder decorator so the block processor can
        // grab its undecorated inner via .Inner when building the per-block recorder.
        builder.AddSingleton<WitnessCapturingHeaderFinder>(ctx =>
            (WitnessCapturingHeaderFinder)ctx.Resolve<IHeaderFinder>());

        // Main-pipeline components in this child scope resolve a session-aware decorator that, when
        // capture is armed, routes calls to a non-caching CodeInfoRepository (so every bytecode
        // lookup flows through IWorldState → proxy → recorder) and, when disarmed, routes back to
        // the cached repository registered at root. Other scopes (block production, RPC simulation,
        // the legacy debug_executionWitness sandbox) are untouched.
        builder.AddDecorator<ICodeInfoRepository>((ctx, repository) =>
        {
            WitnessCaptureSession session = ctx.Resolve<WitnessCaptureSession>();
            return new CodeInfoRepositoryProxy(
                repository,
                ctx.Resolve<IWorldState>(),
                ctx.Resolve<IPrecompileProvider>(),
                () => session.IsActive);
        });

        builder.AddDecorator<IBlockProcessor, WitnessCapturingBlockProcessor>();
    }
}
