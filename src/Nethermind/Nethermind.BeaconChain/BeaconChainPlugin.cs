// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Init.Modules;

namespace Nethermind.BeaconChain;

/// <summary>
/// Embedded consensus-layer driver: follows the Ethereum beacon chain and drives block
/// processing through the engine API handlers, removing the need for an external
/// consensus client.
/// </summary>
public class BeaconChainPlugin(IBeaconChainConfig config) : INethermindPlugin
{
    public string Name => "BeaconChain";
    public string Description => "Embedded Ethereum consensus-layer driver";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;

    private INethermindApi? _api;
    private BeaconChainService? _service;

    public IModule Module => new BeaconChainModule();

    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        // The engine API handlers (MergePlugin) are registered by now.
        _service = _api!.Context.Resolve<BeaconChainService>();
        _api.DisposeStack.Push(_service);
        _ = _service.Start(); // NOTE: Fire and forget, exception handling must be done inside `Start`
        return Task.CompletedTask;
    }
}

public class BeaconChainModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<BeaconChainService>()
            .AddColumnDatabase<BeaconChainDbColumns>("beaconChain")
            ;
    }
}
