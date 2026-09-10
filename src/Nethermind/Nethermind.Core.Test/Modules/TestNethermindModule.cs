// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// For when you don't care if it match prod or not. You just want something that build and will override some
/// component later anyway.
/// </summary>
/// <param name="configProvider"></param>
public class TestNethermindModule(IConfigProvider configProvider, ChainSpec chainSpec, bool useTestSpecProvider = true) : Module
{
    private readonly IReleaseSpec? _releaseSpec;

    public TestNethermindModule(IReleaseSpec? releaseSpec = null) : this(DefaultConfigProvider()) => _releaseSpec = releaseSpec;

    public TestNethermindModule(params IConfig[] configs) : this(DefaultConfigProvider(configs))
    {
    }

    public TestNethermindModule(IConfigProvider configProvider) : this(configProvider, new ChainSpec()
    {
        Parameters = new ChainParameters(),
        Allocations = [],
        Genesis = Build.A.Block
            .WithBlobGasUsed(0) // Non null post 4844
            .TestObject
    })
    {
    }

    public TestNethermindModule(ChainSpec chainSpec) : this(DefaultConfigProvider(), chainSpec)
    {
    }

    public static TestNethermindModule CreateWithRealChainSpec()
    {
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("chainspec/foundation.json");
        return new TestNethermindModule(DefaultConfigProvider(), spec, useTestSpecProvider: false);
    }

    private static ConfigProvider DefaultConfigProvider(params IConfig[] configs) =>
        TestStateBackend.ApplyDefaultBackend(new ConfigProvider(configs), configs);

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        LongDisposeTracker.Configure(builder);

        builder
            .AddModule(new PseudoNethermindModule(chainSpec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, Random.Shared.Next().ToString()));

        if (useTestSpecProvider)
            builder.AddSingleton<ISpecProvider>(_ => new TestSpecProvider(_releaseSpec ?? Osaka.Instance));
    }
}
