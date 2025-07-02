// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Network.Config;

namespace Nethermind.UPnP.Plugin;

public class UPnPPlugin(INetworkConfig networkConfig) : INethermindPlugin
{
    public string Name => "UPnP";
    public string Description => "Automatic port forwarding with UPnP";
    public string Author => "Nethermind";
    public bool Enabled => networkConfig.EnableUPnP;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public IModule Module => new UPnPModule();
}

public class UPnPModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddStep(typeof(UPnPStep));
}
