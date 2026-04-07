// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade;
using Nethermind.Facade.Simulate;
using Nethermind.State.OverridableEnv;
using Nethermind.Init.Modules;
using Nethermind.Core;
using Autofac;

namespace Nethermind.Xdc;

public class XdcBlockchainBridgeFactory : BlockchainBridgeFactory
{
    private readonly ISimulateReadOnlyBlocksProcessingEnvFactory _simulateEnvFactory;
    private readonly IOverridableEnvFactory _envFactory;
    private readonly ILifetimeScope _rootLifetimeScope;

    public XdcBlockchainBridgeFactory(
        ISimulateReadOnlyBlocksProcessingEnvFactory simulateEnvFactory,
        IOverridableEnvFactory envFactory,
        ILifetimeScope rootLifetimeScope)
        : base(simulateEnvFactory, envFactory, rootLifetimeScope)
    {
        _simulateEnvFactory = simulateEnvFactory;
        _envFactory = envFactory;
        _rootLifetimeScope = rootLifetimeScope;
    }

    public override IBlockchainBridge CreateBlockchainBridge()
    {
        IOverridableEnv env = _envFactory.Create();

        ILifetimeScope overridableScopeLifetime = _rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddModule(env)
            .Add<BlockchainBridge.BlockProcessingComponents>());

        // Split it out to isolate the world state and processing components
        IOverridableEnv<BlockchainBridge.BlockProcessingComponents> blockProcessingEnv = overridableScopeLifetime
            .Resolve<IOverridableEnv<BlockchainBridge.BlockProcessingComponents>>();

        ILifetimeScope blockchainBridgeLifetime = _rootLifetimeScope.BeginLifetimeScope((builder) => builder
            .AddScoped<XdcBlockchainBridge>()
            .AddScoped<ISimulateReadOnlyBlocksProcessingEnv>((_) => _simulateEnvFactory.Create())
            .AddScoped(blockProcessingEnv));

        blockchainBridgeLifetime.Disposer.AddInstanceForAsyncDisposal(overridableScopeLifetime);
        _rootLifetimeScope.Disposer.AddInstanceForDisposal(blockchainBridgeLifetime);

        return blockchainBridgeLifetime.Resolve<XdcBlockchainBridge>();
    }
}