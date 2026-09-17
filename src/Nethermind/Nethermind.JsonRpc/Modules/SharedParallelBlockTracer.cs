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
/// workers' processing environments are the expensive part, and a node-wide cap on them is the point. Built the
/// first time a module asks, with the same container configuration the module's own environment gets.</summary>
public sealed class SharedParallelBlockTracer(
    IOverridableEnvFactory envFactory,
    ILifetimeScope rootLifetimeScope,
    IPrefixStateSeedSource prefixSeeds,
    ILogManager logManager,
    Func<ContainerBuilder, ContainerBuilder> configureProcessing)
{
    private readonly Lock _lock = new();
    private ParallelBlockTracer? _tracer;

    /// <summary>Null on a node without the index: nothing to seed from, nothing to build.</summary>
    public IParallelBlockTracer? Get()
    {
        if (!prefixSeeds.Enabled) return null;

        lock (_lock)
        {
            if (_tracer is null)
            {
                _tracer = new ParallelBlockTracer(BuildEnvironment, prefixSeeds, ParallelBlockTracer.Degree, logManager);
                rootLifetimeScope.Disposer.AddInstanceForDisposal(_tracer);
            }

            return _tracer;
        }
    }

    private IOverridableEnv<ParallelBlockTracer.Components> BuildEnvironment()
    {
        IOverridableEnv env = envFactory.Create();
        ILifetimeScope scope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            configureProcessing(builder)
                .AddModule(env)
                .Add<ParallelBlockTracer.Components>());
        return new ParallelBlockTracer.OwnedEnvironment(scope.Resolve<IOverridableEnv<ParallelBlockTracer.Components>>(), scope);
    }
}
