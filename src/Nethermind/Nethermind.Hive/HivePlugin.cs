// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;

namespace Nethermind.Hive;

public class HivePlugin(IHiveConfig hiveConfig) : INethermindPlugin
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public string Name => "Hive";

    public string Description => "Plugin used for executing Hive Ethereum Tests";

    public string Author => "Nethermind";

    public bool Enabled => hiveConfig.Enabled;

    public IModule Module => new HiveModule();
}

public class HiveModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<HiveRunner>()
        .AddStep(typeof(HiveStep));
}
