// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;
using Nethermind.Mcp.Hosting;

namespace Nethermind.Mcp;

/// <summary>
/// Plugin entry point for the Model Context Protocol (MCP) server. Owns the lifecycle of
/// <see cref="McpWebHost"/> and wires <see cref="McpServerPluginModule"/> into the node's
/// Autofac container.
/// </summary>
/// <remarks>
/// Behaviour follows the standard <see cref="INethermindPlugin"/> contract:
/// <list type="bullet">
///   <item><description><see cref="Init"/> only reads config and warns on insecure exposure.</description></item>
///   <item><description><see cref="InitRpcModules"/> starts the Kestrel host (fail-soft).</description></item>
///   <item><description><see cref="DisposeAsync"/> stops and disposes the host.</description></item>
/// </list>
/// MCP failures never crash the node — startup errors are logged and the rest of the node keeps running.
/// </remarks>
public sealed class McpServerPlugin(IMcpConfig config) : INethermindPlugin, IAsyncDisposable
{
    // Config is injected by Autofac via ConfigRegistrationSource so PluginLoader observes
    // `Enabled` *before* Init runs — the plugin table check happens immediately after
    // container resolution, and a config read deferred to Init would always be observed as
    // `false` and the plugin would be filtered out before lifecycle. ILogManager is *not*
    // registered in the plugin container (only IConfig types and ChainSpec are), so the
    // logger is resolved from INethermindApi inside Init, matching the convention used by
    // peer plugins like Flashbots.
    private readonly IMcpConfig _config = config;
    private INethermindApi? _api;
    private ILogger _logger;
    private McpWebHost? _runningHost;

    public string Name => "Mcp";

    public string Description => "Model Context Protocol server";

    public string Author => "Nethermind";

    public bool Enabled => _config.Enabled;

    public bool MustInitialize => false;

    public IModule? Module => new McpServerPluginModule();

    public Task Init(INethermindApi api)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
        _logger = api.LogManager.GetClassLogger<McpServerPlugin>();

        if (IsNonLoopback(_config.HttpHost) && string.IsNullOrEmpty(_config.ApiKey))
        {
            if (_logger.IsWarn)
            {
                _logger.Warn(
                    $"MCP exposed without authentication: HttpHost={_config.HttpHost} but ApiKey is not set. " +
                    "Set Mcp.ApiKey or bind to 127.0.0.1.");
            }
        }

        return Task.CompletedTask;
    }

    public async Task InitRpcModules()
    {
        if (_api is null)
        {
            return;
        }

        if (!Enabled || !_config.HttpEnabled)
        {
            return;
        }

        McpWebHost host = _api.Context.Resolve<McpWebHost>();
        bool started = await host.StartAsync(default).ConfigureAwait(false);
        if (!started)
        {
            // McpWebHost already logged the underlying cause at Error level;
            // re-log here so the failure is unmistakably attributed to the plugin lifecycle.
            if (_logger.IsError) _logger.Error("MCP server failed to start; the node will continue without MCP.");
            return;
        }

        _runningHost = host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_runningHost is null)
        {
            return;
        }

        McpWebHost host = _runningHost;
        _runningHost = null;

        try
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("MCP server stop during plugin dispose threw", ex);
        }

        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("MCP server dispose during plugin dispose threw", ex);
        }
    }

    private static bool IsNonLoopback(string? host) =>
        host is not null
        && !host.Equals("127.0.0.1", StringComparison.Ordinal)
        && !host.Equals("::1", StringComparison.Ordinal)
        && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}
