// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Flashbots.Modules.Flashbots;
using Nethermind.Flashbots.Modules.Rbuilder;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Flashbots;

public class Flashbots(IFlashbotsConfig flashbotsConfig, IJsonRpcConfig jsonRpcConfig) : INethermindPlugin
{
    public virtual string Name => "Flashbots";
    public virtual string Description => "Flashbots";
    public string Author => "Nethermind";
    public bool Enabled => flashbotsConfig.Enabled;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public IModule Module => new FlashbotsModule(flashbotsConfig, jsonRpcConfig);
}

public class FlashbotsModule(IFlashbotsConfig flashbotsConfig, IJsonRpcConfig jsonRpcConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        jsonRpcConfig.EnabledModules = jsonRpcConfig.EnabledModules.Append(ModuleType.Flashbots).ToArray();

        builder
            .RegisterSingletonJsonRpcModule<IRbuilderRpcModule, RbuilderRpcModule>()
            .RegisterBoundedJsonRpcModule<IFlashbotsRpcModule, FlashbotsRpcModuleFactory>(
                flashbotsConfig.FlashbotsModuleConcurrentInstances ?? Environment.ProcessorCount, jsonRpcConfig.Timeout);
        ;
    }
}
