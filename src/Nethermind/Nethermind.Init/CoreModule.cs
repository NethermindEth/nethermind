// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Autofac.Core.Registration;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;

namespace Nethermind.Init;

/// <summary>
/// Nethermind's core module. Registrations might get replaced by plugins.
/// </summary>
public class CoreModule: Module
{
    private readonly INethermindApi _api;
    private readonly IConfigProvider _configProvider;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly ILogManager _logManager;

    public CoreModule(INethermindApi nethermindApi, IConfigProvider configProvider, IJsonSerializer jsonSerializer, ILogManager logManager)
    {
        _api = nethermindApi;
        _configProvider = configProvider;
        _jsonSerializer = jsonSerializer;
        _logManager = logManager;
    }

    protected override void Load(ContainerBuilder builder)
    {
        // TODO: Make it able to dynamically resolve configs.
        builder.RegisterInstance(_configProvider)
            .As<IConfigProvider>();

        builder.RegisterInstance(_api)
            .As<INethermindApi>();

        builder.RegisterInstance(_jsonSerializer)
            .As<IJsonSerializer>();

        builder.RegisterInstance(_logManager)
            .As<ILogManager>();

        builder.RegisterInstance(_api.ChainSpec!);
        builder.RegisterInstance(_api.SpecProvider!)
            .As<ISpecProvider>();

        builder.RegisterInstance(_api.BlockTree!)
            .As<IBlockTree>();
        builder.RegisterInstance(_api.BlockTree!.AsReadOnly())
            .As<IBlockFinder>();
        builder.RegisterInstance(_api.ReceiptStorage!)
            .As<IReceiptStorage>();

        // Obviously this is still shared globally, but we can start detecting which part requires a world state as
        // without explicitly specifying a lifetime, it will crash.
        builder.Register<IWorldState>(_ => _api.WorldStateFactory())
            .InstancePerMatchingLifetimeScope(NethermindScope.WorldState);

        builder.Register<IWitnessCollector>(_ => _api.WitnessCollector!);
    }
}
