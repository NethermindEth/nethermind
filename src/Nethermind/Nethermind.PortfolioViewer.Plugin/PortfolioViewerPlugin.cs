// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Core;

namespace Nethermind.PortfolioViewer.Plugin;

/// <summary>Plugin that serves the portfolio viewer UI on the unauthenticated JSON-RPC HTTP endpoint when
/// <see cref="IPortfolioViewerConfig.Enabled"/> is set.</summary>
public class PortfolioViewerPlugin(IPortfolioViewerConfig config) : INethermindPlugin
{
    /// <inheritdoc/>
    public string Name => "PortfolioViewer";

    /// <inheritdoc/>
    public string Description => "Portfolio viewer UI (balances + NFTs) served at the /portfolio path of the JSON-RPC HTTP endpoint";

    /// <inheritdoc/>
    public string Author => "Nethermind";

    /// <inheritdoc/>
    public bool Enabled => config.Enabled;

    /// <inheritdoc/>
    public IModule Module => new PortfolioViewerModule();
}

/// <summary>Autofac module that registers the portfolio viewer's JSON-RPC service configurer.</summary>
public class PortfolioViewerModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<IJsonRpcServiceConfigurer, PortfolioViewerConfigurer>();
}
