// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.StateComposition;

public class StateCompositionModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.RegisterType<StateCompositionVisitor>()
            .AsSelf()
            .InstancePerDependency();

        builder.RegisterType<StateCompositionStateHolder>()
            .AsSelf()
            .As<IStateCompositionStateHolder>()
            .SingleInstance();

        builder
            .AddSingleton<IStateCompositionService, StateCompositionService>()
            .RegisterSingletonJsonRpcModule<IStateCompositionRpcModule, StateCompositionRpcModule>();
    }
}
