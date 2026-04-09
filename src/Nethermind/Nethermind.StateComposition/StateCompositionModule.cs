// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.StateComposition;

public class StateCompositionModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.AddDatabase(StateCompositionSnapshotStore.DbName);

        builder.RegisterType<StateCompositionStateHolder>()
            .AsSelf()
            .SingleInstance();

        builder
            .AddSingleton<StateCompositionSnapshotStore>()
            .AddSingleton<StateCompositionService>()
            .RegisterSingletonJsonRpcModule<IStateCompositionRpcModule, StateCompositionRpcModule>();
    }
}
