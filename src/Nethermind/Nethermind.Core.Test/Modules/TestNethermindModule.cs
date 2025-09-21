// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// For when you don't care if it match prod or not. You just want something that build and will override some
/// component later anyway.
/// </summary>
/// <param name="configProvider"></param>
public class TestNethermindModule(IConfigProvider configProvider, ChainSpec chainSpec) : Module
{
    private readonly IReleaseSpec? _releaseSpec;

    public TestNethermindModule(IReleaseSpec? releaseSpec = null) : this(new ConfigProvider())
    {
        _releaseSpec = releaseSpec;
    }

    public TestNethermindModule(params IConfig[] configs) : this(new ConfigProvider(configs))
    {
    }

    public TestNethermindModule(IConfigProvider configProvider) : this(configProvider, new ChainSpec()
    {
        Parameters = new ChainParameters(),
        Allocations = new Dictionary<Address, ChainSpecAllocation>(),
        Genesis = Build.A.Block
            .WithBlobGasUsed(0) // Non null post 4844
            .TestObject
    })
    {
    }

    public TestNethermindModule(ChainSpec chainSpec) : this(new ConfigProvider(), chainSpec)
    {
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        LongDisposeTracker.Configure(builder);

        builder
            .AddModule(new PseudoNethermindModule(chainSpec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, Random.Shared.Next().ToString()))
            .AddSingleton<ISpecProvider>(_ => new TestSpecProvider(_releaseSpec ?? Osaka.Instance));
    }
}
