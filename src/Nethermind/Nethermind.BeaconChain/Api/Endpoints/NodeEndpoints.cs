// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary><c>/eth/v1/node/*</c>: health, identity, version, syncing, and peer accounting.</summary>
internal static class NodeEndpoints
{
    /// <summary>Slots the head may lag the wall clock by and still count as caught up (health 200, is_syncing=false).</summary>
    private const long ReadySyncDistance = 1;

    public static void Map(WebApplication app, BeaconApiContext ctx)
    {
        app.MapGet("/eth/v1/node/health", c => Health(c, ctx));
        app.MapGet("/eth/v1/node/version", c => Version(c));
        app.MapGet("/eth/v1/node/identity", c => Identity(c, ctx));
        app.MapGet("/eth/v1/node/syncing", c => Syncing(c, ctx));
        app.MapGet("/eth/v1/node/peer_count", c => PeerCount(c, ctx));

        // PeerManager tracks connected sessions only (address -> session), not a peer's real libp2p
        // peer id, direction or connection-state history, so the per-peer listing fields the spec
        // requires cannot be filled in truthfully.
        const string peersGap = "Per-peer identity, state and direction are not tracked by this driver's peer manager; only a connected-peer count is available from /eth/v1/node/peer_count.";
        app.MapGet("/eth/v1/node/peers", c => ApiErrors.Write(c, StatusCodes.Status501NotImplemented, peersGap, c.RequestAborted));
        app.MapGet("/eth/v1/node/peers/{peer_id}", c => ApiErrors.Write(c, StatusCodes.Status501NotImplemented, peersGap, c.RequestAborted));
    }

    private static Task Health(HttpContext c, BeaconApiContext ctx)
    {
        StatusMessageV2 status = ctx.StatusSource.CurrentStatus;
        if (status.HeadRoot == Hash256.Zero)
        {
            // Nothing has been observed yet: neither "ready" nor "syncing" is true, only "not initialized".
            c.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }

        long distance = (long)ctx.SlotClock.CurrentSlot - (long)status.HeadSlot;
        c.Response.StatusCode = distance > ReadySyncDistance ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        return Task.CompletedTask;
    }

    private static Task Version(HttpContext c)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        return BeaconApiJson.WriteDataAsync(c, new VersionDto(ClientIdentity.AgentVersion), c.RequestAborted);
    }

    private static Task Identity(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (ctx.P2P?.LocalPeerId is not { } peerId)
        {
            return ApiErrors.Write(c, StatusCodes.Status503ServiceUnavailable,
                "The P2P host has not started yet; no peer id is assigned.", c.RequestAborted);
        }

        string peerIdString = peerId.ToString()!;
        string[] p2pAddresses = new string[ctx.P2P.ListenAddresses.Count];
        for (int i = 0; i < p2pAddresses.Length; i++)
        {
            p2pAddresses[i] = $"{ctx.P2P.ListenAddresses[i]}/p2p/{peerIdString}";
        }

        MetadataDto metadata = new(
            ctx.MetadataSource.Current.SeqNumber.ToString(),
            HexConvert.BitVectorToHex(ctx.MetadataSource.Current.Attnets!),
            HexConvert.BitVectorToHex(ctx.MetadataSource.Current.Syncnets!));

        IdentityDto dto = new(
            peerIdString,
            ctx.Discovery?.LocalNodeRecord.ToString(),
            p2pAddresses,
            // Not independently derivable: this host does not compute a separate discovery-only
            // multiaddr set (see the report for why), so it is left honestly empty rather than
            // duplicating p2p_addresses under a different label.
            [],
            metadata);

        return BeaconApiJson.WriteDataAsync(c, dto, c.RequestAborted);
    }

    private static Task Syncing(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        StatusMessageV2 status = ctx.StatusSource.CurrentStatus;
        ulong wallSlot = ctx.SlotClock.CurrentSlot;
        long distance = (long)wallSlot - (long)status.HeadSlot;
        if (distance < 0) distance = 0;

        SyncingDto dto = new(
            status.HeadSlot.ToString(),
            distance.ToString(),
            distance > ReadySyncDistance,
            ResponseEnvelope.ExecutionOptimistic(),
            // IEngineDriver only exposes LastNewPayloadStatus, not the forkchoiceUpdated result, and
            // EngineDriver swallows engine-call exceptions and reports SYNCING instead (see
            // EngineDriver.Unwrap's remarks) — real EL outages and genuine EL sync are
            // indistinguishable from any signal available here. The one thing this can say
            // truthfully is whether the engine has ever answered a newPayload call at all.
            ctx.Engine.LastNewPayloadStatus is null);

        return BeaconApiJson.WriteDataAsync(c, dto, c.RequestAborted);
    }

    private static Task PeerCount(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        // This driver's peer manager only ever holds connected, status-exchanged peers: there is no
        // separate "connecting"/"disconnecting" state machine, so those counts are genuinely zero
        // rather than unknown.
        PeerCountDto dto = new(
            "0",
            "0",
            (ctx.PeerManager?.PeerCount ?? 0).ToString(),
            "0");

        return BeaconApiJson.WriteDataAsync(c, dto, c.RequestAborted);
    }

    private sealed record VersionDto([property: JsonPropertyName("version")] string Version);

    private sealed record MetadataDto(
        [property: JsonPropertyName("seq_number")] string SeqNumber,
        [property: JsonPropertyName("attnets")] string Attnets,
        [property: JsonPropertyName("syncnets")] string Syncnets);

    private sealed record IdentityDto(
        [property: JsonPropertyName("peer_id")] string PeerId,
        [property: JsonPropertyName("enr")] string? Enr,
        [property: JsonPropertyName("p2p_addresses")] string[] P2PAddresses,
        [property: JsonPropertyName("discovery_addresses")] string[] DiscoveryAddresses,
        [property: JsonPropertyName("metadata")] MetadataDto Metadata);

    private sealed record SyncingDto(
        [property: JsonPropertyName("head_slot")] string HeadSlot,
        [property: JsonPropertyName("sync_distance")] string SyncDistance,
        [property: JsonPropertyName("is_syncing")] bool IsSyncing,
        [property: JsonPropertyName("is_optimistic")] bool IsOptimistic,
        [property: JsonPropertyName("el_offline")] bool ElOffline);

    private sealed record PeerCountDto(
        [property: JsonPropertyName("disconnected")] string Disconnected,
        [property: JsonPropertyName("connecting")] string Connecting,
        [property: JsonPropertyName("connected")] string Connected,
        [property: JsonPropertyName("disconnecting")] string Disconnecting);
}
