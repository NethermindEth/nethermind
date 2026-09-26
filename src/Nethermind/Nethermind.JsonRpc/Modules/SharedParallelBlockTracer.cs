// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;

namespace Nethermind.JsonRpc.Modules;

/// <summary>One parallel block tracer per module factory, shared by every module instance the factory creates: the
/// environments are retained per factory; active workers share the node-wide budget. Built the
/// first time a module asks, with the same container configuration the module's own environment gets.</summary>
public sealed class SharedParallelBlockTracer(
    ITraceEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IPrefixStateSeedSource prefixSeeds,
    ParallelTraceBudgets budgets,
    ILogManager logManager,
    Func<ContainerBuilder, ContainerBuilder> configureProcessing)
{
    private readonly Lock _lock = new();
    private ParallelBlockTracer? _tracer;

    /// <summary>Null when nothing can seed a block, or when no seed this chain can take allows two workers.</summary>
    public IParallelBlockTracer? Get()
    {
        if (!prefixSeeds.Enabled || !budgets.AllowsParallelTracing) return null;

        lock (_lock)
        {
            if (_tracer is null)
            {
                _tracer = new ParallelBlockTracer(BuildEnvironment, prefixSeeds, budgets, logManager);
                rootLifetimeScope.Disposer.AddInstanceForDisposal(_tracer);
            }

            return _tracer;
        }
    }

    private IOverridableEnv<ParallelBlockTracer.Components> BuildEnvironment()
    {
        IOverridableEnv env = envFactory.CreateForTracing();
        ILifetimeScope scope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            configureProcessing(builder)
                .AddModule(env)
                .Add<ParallelBlockTracer.Components>());
        return new ParallelBlockTracer.OwnedEnvironment(scope.Resolve<IOverridableEnv<ParallelBlockTracer.Components>>(), scope);
    }
}
