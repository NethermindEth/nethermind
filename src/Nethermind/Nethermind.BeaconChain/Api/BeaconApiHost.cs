// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Api.Endpoints;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
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
    BeaconDiscovery? discovery = null,
    ForkChoiceSnapshotHolder? forkChoiceSnapshots = null,
    HeadSnapshotHolder? headSnapshots = null,
    DataColumnSidecarPool? columnPool = null,
    BeaconChainService? driver = null) : IAsyncDisposable
{
    /// <summary>Keeps routing from matching on <c>Content-Type</c>, which would hide a mismatch behind the fallback's 404.</summary>
    private const string AnyContentType = "*/*";

    private readonly ILogger _logger = logManager.GetClassLogger<BeaconApiHost>();
    private WebApplication? _app;
    private StateRequestLimiter? _stateLimiter;
    private int _port;
    private CancellationTokenRegistration _exitRegistration;
    private CancellationTokenRegistration _driverRegistration;
    private int _disposed;

    /// <summary>The port actually bound; differs from <see cref="IBeaconApiConfig.Port"/> only when
    /// that config requested an ephemeral port (0), as tests do.</summary>
    public int Port => Volatile.Read(ref _port);

    public async Task StartAsync(CancellationToken token)
    {
        StateRequestLimiter stateLimiter = _stateLimiter = new StateRequestLimiter(apiConfig);
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore().UseUrls($"http://{apiConfig.Host}:{apiConfig.Port}");
        builder.Services.AddRoutingCore();
        BeaconApiContext ctx = new(chainConfig, spec, statusSource, slotClock, store, metadataSource, engine, logManager, p2p, peerManager, discovery, forkChoiceSnapshots, headSnapshots, columnPool);
        BeaconApiEndpoints.AddServices(builder.Services, ctx);

        WebApplication app = builder.Build();
        if (IsLoopbackHost(apiConfig.Host))
            app.Use(ValidateLoopbackRequest);
        app.Use(stateLimiter.InvokeAsync);
        BeaconApiEndpoints.UseMiddleware(app, logManager);
        MapRoutes(app, ctx);

        await app.StartAsync(token);
        int port = new Uri(app.Urls.First()).Port;
        // Publish the port before the app: a thread that observes _app via the volatile read below
        // must also see the already-final Port.
        Volatile.Write(ref _port, port);
        Volatile.Write(ref _app, app);

        // The API must not outlive the node: stop it the moment the process starts exiting, rather
        // than waiting for whatever step in the shutdown sequence eventually disposes the plugin.
        // The registration is disposed in DisposeAsync so a later exit never re-enters a disposed host.
        _exitRegistration = processExitSource.Token.Register(static state => _ = ((BeaconApiHost)state!).StopAsync(), this);
        _driverRegistration = driver?.Stopping.Register(static state => _ = ((BeaconApiHost)state!).StopAsync(), this) ?? default;

        if (_logger.IsInfo) _logger.Info($"Beacon API listening on http://{apiConfig.Host}:{port}");
    }

    /// <summary>Maps every route onto its handler; the services of <see cref="BeaconApiEndpoints.AddServices"/> must be registered with the same <paramref name="ctx"/>.</summary>
    internal static void MapRoutes(WebApplication app, BeaconApiContext ctx)
    {
        RouteGroupBuilder json = app.MapGroup("").ServesJson();
        RouteGroupBuilder sszOrJson = app.MapGroup("").ServesSszOrJson();

        app.MapGet("/eth/v1/node/health", (NodeEndpoints node, int? syncing_status) => node.Health(syncing_status));
        json.MapGet("/eth/v1/node/version", NodeEndpoints.Version);
        json.MapGet("/eth/v1/node/identity", (NodeEndpoints node) => node.Identity());
        json.MapGet("/eth/v1/node/syncing", (NodeEndpoints node) => node.Syncing());
        json.MapGet("/eth/v1/node/peer_count", (NodeEndpoints node) => node.PeerCount());
        json.MapGet("/eth/v1/node/peers", (NodeEndpoints node, string[] state, string[] direction) => node.Peers(state, direction));
        json.MapGet("/eth/v1/node/peers/{peer_id}", (NodeEndpoints node, string peer_id) => node.PeerById(peer_id));

        json.MapGet("/eth/v1/config/spec", (ConfigEndpoints config) => config.Spec());
        json.MapGet("/eth/v1/config/fork_schedule", (ConfigEndpoints config) => config.ForkSchedule());
        json.MapGet("/eth/v1/config/deposit_contract", (ConfigEndpoints config) => config.DepositContract());

        json.MapGet("/eth/v1/beacon/genesis", (BeaconEndpoints beacon) => beacon.Genesis());
        json.MapGet("/eth/v1/beacon/headers", (BeaconEndpoints beacon, string? parent_root, string? slot) => beacon.HeaderList(parent_root, slot));
        json.MapGet("/eth/v1/beacon/headers/{block_id}", (BeaconEndpoints beacon, string block_id) => beacon.HeaderById(block_id));
        json.MapGet("/eth/v1/beacon/blocks/{block_id}/root", (BeaconEndpoints beacon, string block_id) => beacon.BlockRoot(block_id));
        sszOrJson.MapGet("/eth/v2/beacon/blocks/{block_id}", (BeaconEndpoints beacon, string block_id) => beacon.BlockContent(block_id));
        json.MapGet("/eth/v2/beacon/blocks/{block_id}/attestations", (BeaconEndpoints beacon, string block_id) => beacon.BlockAttestations(block_id));
        sszOrJson.MapGet("/eth/v1/beacon/execution_payload_envelopes/{block_id}", (BeaconEndpoints beacon, string block_id) => beacon.ExecutionPayloadEnvelope(block_id));
        sszOrJson.MapGet("/eth/v1/beacon/blobs/{block_id}", (BlobsEndpoint blobs, string block_id, string[] versioned_hashes) => blobs.GetBlobs(block_id, versioned_hashes));

        json.MapGet("/eth/v1/beacon/states/{state_id}/fork", (BeaconStatesEndpoints states, string state_id) => states.Fork(state_id));
        json.MapGet("/eth/v1/beacon/states/{state_id}/root", (BeaconStatesEndpoints states, string state_id) => states.Root(state_id));
        json.MapGet("/eth/v1/beacon/states/{state_id}/finality_checkpoints", (BeaconStatesEndpoints states, string state_id) => states.FinalityCheckpoints(state_id));
        json.MapGet("/eth/v1/beacon/states/{state_id}/validators", (BeaconStatesEndpoints states, string state_id, QueryValues id, QueryValues status) => states.Validators(state_id, id.Values, status.Values));
        json.MapGet("/eth/v1/beacon/states/{state_id}/validators/{validator_id}", (BeaconStatesEndpoints states, string state_id, string validator_id) => states.ValidatorById(state_id, validator_id));
        json.MapGet("/eth/v1/beacon/states/{state_id}/validator_balances", (BeaconStatesEndpoints states, string state_id, QueryValues id) => states.ValidatorBalances(state_id, id.Values));
        json.MapPost("/eth/v1/beacon/states/{state_id}/validators", (BeaconStatesEndpoints states, string state_id, [FromBody] BeaconStatesEndpoints.ValidatorsRequestDto request) => states.ValidatorsPost(state_id, request)).Accepts<BeaconStatesEndpoints.ValidatorsRequestDto>(AnyContentType);
        json.MapPost("/eth/v1/beacon/states/{state_id}/validator_balances", (BeaconStatesEndpoints states, string state_id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ImmutableArray<string> ids) => states.ValidatorBalancesPost(state_id, ids)).Accepts<string[]>(AnyContentType);
        sszOrJson.MapPost("/eth/v1/beacon/states/{state_id}/validator_identities", (BeaconStatesEndpoints states, string state_id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ImmutableArray<string> ids) => states.ValidatorIdentities(state_id, ids)).Accepts<string[]>(AnyContentType);
        json.MapGet("/eth/v1/beacon/states/{state_id}/committees", (BeaconStatesEndpoints states, string state_id, ulong? epoch, int? index, ulong? slot) => states.Committees(state_id, epoch, index, slot));
        sszOrJson.MapGet("/eth/v1/beacon/states/{state_id}/pending_deposits", (BeaconStatesEndpoints states, string state_id) => states.PendingDeposits(state_id));
        sszOrJson.MapGet("/eth/v1/beacon/states/{state_id}/pending_partial_withdrawals", (BeaconStatesEndpoints states, string state_id) => states.PendingPartialWithdrawals(state_id));
        sszOrJson.MapGet("/eth/v1/beacon/states/{state_id}/pending_consolidations", (BeaconStatesEndpoints states, string state_id) => states.PendingConsolidations(state_id));
        sszOrJson.MapGet("/eth/v1/beacon/states/{state_id}/proposer_lookahead", (BeaconStatesEndpoints states, string state_id) => states.ProposerLookahead(state_id));
        json.MapGet("/eth/v1/beacon/states/{state_id}/randao", (BeaconStatesEndpoints states, string state_id, ulong? epoch) => states.Randao(state_id, epoch));
        json.MapGet("/eth/v1/beacon/states/{state_id}/sync_committees", (BeaconStatesEndpoints states, string state_id, ulong? epoch) => states.SyncCommittees(state_id, epoch));

        sszOrJson.MapGet("/eth/v2/debug/beacon/states/{state_id}", (DebugEndpoints debug, string state_id) => debug.State(state_id));
        json.MapGet("/eth/v1/debug/beacon/fork_choice", (DebugEndpoints debug) => debug.ForkChoice());
        json.MapGet("/eth/v1/debug/fork_choice", (DebugEndpoints debug) => debug.ForkChoice());

        app.MapMethods("/eth/v1/validator/{**path}", ["GET", "POST"], ValidatorEndpoints.NotServed);
        app.MapGet("/eth/v1/events", (HttpContext c) => EventsEndpoint.Stream(c, ctx, app.Lifetime.ApplicationStopping));

        app.MapFallback(c => ApiErrors.Write(c, StatusCodes.Status404NotFound, "Unknown endpoint", c.RequestAborted));
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address));

    private static Task ValidateLoopbackRequest(HttpContext context, RequestDelegate next)
    {
        if (!IsLoopbackHost(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        string origin = context.Request.Headers.Origin.ToString();
        string fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if ((origin.Length > 0 && !origin.Equals($"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)) ||
            (fetchSite.Length > 0 && fetchSite is not "same-origin" and not "none"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }

    public async Task StopAsync()
    {
        WebApplication? app = Volatile.Read(ref _app);
        if (app is not null)
        {
            try
            {
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
                await app.StopAsync(deadline.Token);
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

        await _exitRegistration.DisposeAsync();
        await _driverRegistration.DisposeAsync();
        await StopAsync();

        WebApplication? app = Volatile.Read(ref _app);
        if (app is not null)
        {
            await app.DisposeAsync();
        }

        _stateLimiter?.Dispose();
    }
}
