// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Core;

namespace Nethermind.BalanceViewer.Plugin;

public class BalanceViewerPlugin(IBalanceViewerConfig config) : INethermindPlugin
{
    public string Name => "BalanceViewer";
    public string Description => "Balance viewer UI served at the /balances path of the JSON-RPC HTTP endpoint";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;
    public IModule Module => new BalanceViewerModule();
}

public class BalanceViewerModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<ISiblingNodeRegistry, SiblingNodeRegistry>()
        .AddSingleton<IJsonRpcServiceConfigurer, BalanceViewerConfigurer>();
}
