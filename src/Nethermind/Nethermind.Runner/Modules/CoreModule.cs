// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Consensus;

namespace Nethermind.Runner.Modules;

public class CoreModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.RegisterType<FollowOtherMiners>()
            .As<IGasLimitCalculator>()
            .SingleInstance();

        builder.RegisterType<NethermindApi>()
            .As<INethermindApi>()
            .SingleInstance();
    }
}
