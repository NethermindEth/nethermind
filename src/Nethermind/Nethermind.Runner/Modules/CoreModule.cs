// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Consensus;
using Nethermind.Core.Container;
using Module = Autofac.Module;

namespace Nethermind.Runner.Modules;

/// <summary>
/// CoreModule should be something Nethermind specific
/// </summary>
public class CoreModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<IGasLimitCalculator, FollowOtherMiners>()
            .AddSingleton<INethermindApi, NethermindApi>();

        builder.RegisterSource(new FallbackToFieldFromApi<INethermindApi>());
    }
}
