// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Core;

namespace Nethermind.PortfolioViewer.Plugin;

public class PortfolioViewerPlugin(IPortfolioViewerConfig config) : INethermindPlugin
{
    public string Name => "PortfolioViewer";
    public string Description => "Portfolio viewer UI (balances + NFTs) served at the /portfolio path of the JSON-RPC HTTP endpoint";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;
    public IModule Module => new PortfolioViewerModule();
}

public class PortfolioViewerModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<IJsonRpcServiceConfigurer, PortfolioViewerConfigurer>();
}
