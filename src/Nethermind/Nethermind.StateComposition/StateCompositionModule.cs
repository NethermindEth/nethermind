// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.StateComposition.Rpc;
using Nethermind.StateComposition.Service;
using Nethermind.StateComposition.Snapshots;

namespace Nethermind.StateComposition;

public class StateCompositionModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.AddDatabase(StateCompositionSnapshotStore.DbName);

        builder
            .AddSingleton<StateCompositionStateHolder>()
            .AddSingleton<StateCompositionSnapshotStore>()
            .RegisterSingletonJsonRpcModule<IStateCompositionRpcModule, StateCompositionRpcModule>();

        // AutoActivate so the service constructs itself as soon as the container
        // is built, wiring its NewHeadBlock subscription without requiring the
        // plugin's Init method to pull it from the context. RegisterType + AsSelf
        // mirrors the behaviour of AddSingleton<T>() plus Autofac's AutoActivate
        // startup hook.
        builder.RegisterType<StateCompositionService>()
            .AsSelf()
            .SingleInstance()
            .AutoActivate();
    }
}
