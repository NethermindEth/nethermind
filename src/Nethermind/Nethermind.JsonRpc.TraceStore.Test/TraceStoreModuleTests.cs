// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Test;

public class TraceStoreModuleTests
{
    [Test]
    public void Registers_db_persisting_block_tracer_for_main_processor_only()
    {
        using IContainer container = BuildContainer(new TraceStoreConfig { Enabled = true });

        // The tracer is contributed via an IMainProcessingModule, so it must NOT be resolvable at the root.
        Assert.That(container.Resolve<IEnumerable<IBlockTracer>>(), Is.Empty);

        // Resolving the tracer also resolves its dependencies (the shared serializer and the keyed DB).
        IBlockTracer[] tracers = ResolveMainProcessingTracers(container);
        Assert.That(tracers, Has.One.InstanceOf<DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer>>());
    }

    private static IContainer BuildContainer(ITraceStoreConfig config) => new ContainerBuilder()
        .AddSingleton<ILogManager>(LimboLogs.Instance)
        .AddSingleton<IDbFactory>(new MemDbFactory())
        .AddSingleton(config)
        .AddModule(new TraceStorePlugin(config).Module!)
        .Build();

    // Mirrors how MainProcessingContext applies the registered IMainProcessingModules and seeds the tracers.
    private static IBlockTracer[] ResolveMainProcessingTracers(IContainer root)
    {
        IMainProcessingModule[] modules = root.Resolve<IMainProcessingModule[]>();
        ILifetimeScope scope = root.BeginLifetimeScope(builder => builder.AddModule(modules));
        return scope.Resolve<IEnumerable<IBlockTracer>>().ToArray();
    }
}
