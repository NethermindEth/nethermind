// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autofac;
using Autofac.Core;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.OpcodeTracing.Plugin.Tracing;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.OpcodeTracing.Plugin.Test;

public class OpcodeTracingModuleTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp() => _tempDir = Path.Combine(Path.GetTempPath(), "opcodetrace-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort cleanup */ }
    }

    [Test]
    public void RealTime_mode_registers_block_tracer_and_no_step()
    {
        using IContainer container = BuildContainer(new OpcodeTracingConfig
        {
            Enabled = true,
            Mode = "RealTime",
            RecentBlocks = 5,
            OutputDirectory = _tempDir
        });

        Assert.That(container.Resolve<IEnumerable<StepInfo>>(), Is.Empty);
        Assert.That(ResolveMainProcessingTracers(container), Has.One.InstanceOf<OpcodeBlockTracer>());
    }

    [Test]
    public void RealTime_mode_with_invalid_config_throws()
    {
        using IContainer container = BuildContainer(new OpcodeTracingConfig
        {
            Enabled = true,
            Mode = "RealTime",
            StartBlock = 10,
            EndBlock = 1,
            OutputDirectory = _tempDir
        });

        // Invalid config makes PrepareAsync throw while resolving the live tracer, aborting startup by design
        // rather than silently running with tracing off.
        Assert.That(() => ResolveMainProcessingTracers(container), Throws.InstanceOf<DependencyResolutionException>());
    }

    [Test]
    public void Retrospective_mode_registers_step_and_no_block_tracer()
    {
        using IContainer container = BuildContainer(new OpcodeTracingConfig
        {
            Enabled = true,
            Mode = "Retrospective",
            StartBlock = 1,
            EndBlock = 3,
            OutputDirectory = _tempDir
        });

        Assert.That(ResolveMainProcessingTracers(container), Is.Empty);
        Assert.That(container.Resolve<IEnumerable<StepInfo>>().Select(static s => s.StepType),
            Has.Member(typeof(StartOpcodeRetrospectiveTracing)));
    }

    private IContainer BuildContainer(IOpcodeTracingConfig config) =>
        new ContainerBuilder()
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton(Substitute.For<IBlockTree>())
            .AddSingleton(Substitute.For<ISpecProvider>())
            .AddSingleton(Substitute.For<IEthereumEcdsa>())
            .AddSingleton(Substitute.For<ISyncModeSelector>())
            .AddSingleton(Substitute.For<IReadOnlyTxProcessingEnvFactory>())
            .AddModule(new OpcodeTracingModule(config))
            .Build();

    // Mirrors how MainProcessingContext applies the registered IMainProcessingModules and resolves the tracers.
    private static IBlockTracer[] ResolveMainProcessingTracers(IContainer root)
    {
        IMainProcessingModule[] modules = root.Resolve<IMainProcessingModule[]>();
        ILifetimeScope scope = root.BeginLifetimeScope(builder => builder.AddModule(modules));
        return scope.Resolve<IEnumerable<IBlockTracer>>().ToArray();
    }
}
