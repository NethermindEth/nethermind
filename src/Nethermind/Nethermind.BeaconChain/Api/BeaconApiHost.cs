// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Config;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.BeaconChain.Api;

/// <summary>
/// Owns the Beacon API's own Kestrel instance: a separate listener from the Engine/JSON-RPC host,
/// with no JWT authentication and no trusted-port middleware, started and stopped with the plugin's
/// lifetime and cancelled from the process exit token.
/// </summary>
public sealed class BeaconApiHost(
    IBeaconApiConfig apiConfig,
    IBeaconChainConfig chainConfig,
    BeaconChainSpec spec,
    IBeaconChainStatusSource statusSource,
    SlotClock slotClock,
    BeaconChainStore store,
    LocalMetadataSource metadataSource,
    IEngineDriver engine,
    IProcessExitSource processExitSource,
    ILogManager logManager,
    BeaconP2P? p2p = null,
    PeerManager? peerManager = null,
    BeaconDiscovery? discovery = null) : IAsyncDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<BeaconApiHost>();
    private WebApplication? _app;
    private int _disposed;

    /// <summary>The port actually bound; differs from <see cref="IBeaconApiConfig.Port"/> only when
    /// that config requested an ephemeral port (0), as tests do.</summary>
    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken token)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{apiConfig.Host}:{apiConfig.Port}");

        WebApplication app = builder.Build();
        BeaconApiContext ctx = new(chainConfig, spec, statusSource, slotClock, store, metadataSource, engine, logManager, p2p, peerManager, discovery);
        BeaconApiEndpoints.MapAll(app, ctx);

        await app.StartAsync(token);
        _app = app;
        Port = new Uri(app.Urls.First()).Port;

        // The API must not outlive the node: stop it the moment the process starts exiting, rather
        // than waiting for whatever step in the shutdown sequence eventually disposes the plugin.
        processExitSource.Token.Register(static state => _ = ((BeaconApiHost)state!).StopAsync(), this);

        if (_logger.IsInfo) _logger.Info($"Beacon API listening on http://{apiConfig.Host}:{Port}");
    }

    public async Task StopAsync()
    {
        WebApplication? app = _app;
        if (app is not null)
        {
            try
            {
                await app.StopAsync();
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error stopping the Beacon API host.", e);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
