// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;

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
        // Expose the decorator under its concrete type so the block-processor decorator can take it
        // directly (Autofac doesn't model decorator chains as typed registrations). The factory's
        // IWorldState arg is the decorated chain — scoped to match IWorldState's lifetime.
        builder.AddScoped<WitnessCapturingWorldStateProxy, IWorldState>(
            static worldState => AsOutermost<IWorldState, WitnessCapturingWorldStateProxy>(worldState));

        builder.AddDecorator<IHeaderFinder, WitnessCapturingHeaderFinder>();
        // Same bridge for the header-finder decorator so the block processor can grab its undecorated
        // inner via .Inner when building the per-block recorder. Singleton to match IHeaderFinder.
        builder.AddSingleton<WitnessCapturingHeaderFinder, IHeaderFinder>(
            static headerFinder => AsOutermost<IHeaderFinder, WitnessCapturingHeaderFinder>(headerFinder));

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

    // The bridges above assume the witness decorator is the outermost wrapper of its service. That
    // holds today, but a decorator added by a later-running module would shift the outermost instance;
    // surface that as an actionable startup error rather than an opaque InvalidCastException.
    private static TDecorator AsOutermost<TService, TDecorator>(TService resolved)
        where TService : class
        where TDecorator : class, TService
        => resolved as TDecorator
           ?? throw new InvalidOperationException(
               $"{nameof(WitnessCapturingMainProcessingModule)} expected the outermost {typeof(TService).Name} " +
               $"to be {typeof(TDecorator).Name}, but resolved {resolved.GetType().Name}. Another decorator was " +
               $"registered on {typeof(TService).Name} after this module — keep the witness decorator outermost, " +
               $"or expose it under its concrete type without relying on decorator order.");
}
