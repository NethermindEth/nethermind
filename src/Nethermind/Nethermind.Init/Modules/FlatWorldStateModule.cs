// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core;
using Nethermind.State;
using Nethermind.State.Flat;

namespace Nethermind.Init.Modules;

public class FlatWorldStateModule: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.AddSingleton<MainPruningTrieStoreFactory>(_ => throw new Exception($"{nameof(MainPruningTrieStoreFactory)} disabled."));
        builder.AddSingleton<PruningTrieStateFactory>(_ => throw new Exception($"{nameof(PruningTrieStateFactory)} disabled."));


        builder
            .AddSingleton<IWorldStateManager, FlatWorldStateManager>()
            .Map<IStateReader, IWorldStateManager>((m) => m.GlobalStateReader);
    }
}
