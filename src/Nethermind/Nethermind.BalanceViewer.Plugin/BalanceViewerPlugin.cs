// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Core;

namespace Nethermind.BalanceViewer.Plugin;

public class BalanceViewerPlugin(IPortfolioConfig config) : INethermindPlugin
{
    public string Name => "Portfolio";
    public string Description => "Portfolio UI (balances + NFTs) served at the /portfolio path of the JSON-RPC HTTP endpoint";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;
    public IModule Module => new BalanceViewerModule();
}

public class BalanceViewerModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<IJsonRpcServiceConfigurer, BalanceViewerConfigurer>();
}
