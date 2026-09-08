// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Db;
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
/// <param name="configProvider">The configuration provider used by the test container.</param>
/// <param name="chainSpec">The chain specification used by the test container.</param>
/// <param name="useTestSpecProvider">Whether to replace the chain specification provider with a test provider.</param>
/// <param name="preserveFlatDbConfig">Whether to preserve the provider's flat DB selection instead of applying the test-suite default.</param>
public class TestNethermindModule(
    IConfigProvider configProvider,
    ChainSpec chainSpec,
    bool useTestSpecProvider = true,
    bool preserveFlatDbConfig = false) : Module
{
    private readonly IReleaseSpec? _releaseSpec;

    public TestNethermindModule(IReleaseSpec? releaseSpec = null) : this(CreateConfigProvider(), CreateDefaultChainSpec(), preserveFlatDbConfig: true) => _releaseSpec = releaseSpec;

    public TestNethermindModule(params IConfig[] configs) : this(CreateConfigProvider(configs), CreateDefaultChainSpec(), preserveFlatDbConfig: true)
    {
    }

    public TestNethermindModule(IConfigProvider configProvider, bool preserveFlatDbConfig = false) :
        this(configProvider, CreateDefaultChainSpec(), preserveFlatDbConfig: preserveFlatDbConfig)
    {
    }

    public TestNethermindModule(ChainSpec chainSpec) : this(CreateConfigProvider(), chainSpec, preserveFlatDbConfig: true)
    {
    }

    public static TestNethermindModule CreateWithRealChainSpec()
    {
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("chainspec/foundation.json");
        return new TestNethermindModule(CreateConfigProvider(), spec, useTestSpecProvider: false, preserveFlatDbConfig: true);
    }

    private static ChainSpec CreateDefaultChainSpec() => new()
    {
        Parameters = new ChainParameters(),
        Allocations = [],
        Genesis = Build.A.Block
            .WithBlobGasUsed(0) // Non null post 4844
            .TestObject
    };

    private static ConfigProvider CreateConfigProvider(params IConfig[] configs) =>
        new([new FlatDbConfig { Enabled = Blockchain.TestBlockchain.UseFlatDbByDefault }, .. configs]);

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        if (!preserveFlatDbConfig)
        {
            configProvider.GetConfig<IFlatDbConfig>().Enabled = Blockchain.TestBlockchain.UseFlatDbByDefault;
        }

        LongDisposeTracker.Configure(builder);

        builder
            .AddModule(new PseudoNethermindModule(chainSpec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, Random.Shared.Next().ToString()));

        if (useTestSpecProvider)
            builder.AddSingleton<ISpecProvider>(_ => new TestSpecProvider(_releaseSpec ?? Osaka.Instance));
    }
}
