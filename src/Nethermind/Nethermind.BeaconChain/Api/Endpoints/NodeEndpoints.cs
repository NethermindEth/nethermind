// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
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
        app.MapGet("/eth/v1/node/peers", c => Peers(c, ctx));
        app.MapGet("/eth/v1/node/peers/{peer_id}", (HttpContext c, string peer_id) => PeerById(c, peer_id, ctx));
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
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            // EngineDriver swallows engine-call exceptions and reports SYNCING instead (see
            // EngineDriver.Unwrap's remarks) - real EL outages and genuine EL sync are
            // indistinguishable from any signal available here. The one thing this can say
            // truthfully is whether the engine has ever answered a newPayload call at all.
            !ctx.Engine.HasAnsweredNewPayload);

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

    /// <summary>
    /// <c>/eth/v1/node/peers</c>: every peer this driver's peer manager currently tracks, optionally
    /// narrowed by the repeatable <c>state</c> and <c>direction</c> query parameters. A missing peer
    /// manager reports an empty list rather than an error - the same "zero rather than unavailable"
    /// choice <see cref="PeerCount"/> already makes for the same case.
    /// </summary>
    private static Task Peers(HttpContext c, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        List<PeerConnectionState> states = [];
        foreach (string? raw in c.Request.Query["state"])
        {
            if (ParseState(raw) is not { } state)
            {
                return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Unknown peer state '{raw}'.", c.RequestAborted);
            }

            states.Add(state);
        }

        List<PeerDirection> directions = [];
        foreach (string? raw in c.Request.Query["direction"])
        {
            if (ParseDirection(raw) is not { } direction)
            {
                return ApiErrors.Write(c, StatusCodes.Status400BadRequest, $"Unknown peer direction '{raw}'.", c.RequestAborted);
            }

            directions.Add(direction);
        }

        IReadOnlyList<PeerRecord> peers = ctx.PeerManager?.Peers ?? [];
        List<PeerDto> filtered = new(peers.Count);
        foreach (PeerRecord peer in peers)
        {
            if (states.Count > 0 && !states.Contains(peer.State))
            {
                continue;
            }

            if (directions.Count > 0 && !directions.Contains(peer.Direction))
            {
                continue;
            }

            filtered.Add(ToPeerDto(peer));
        }

        c.Response.ContentType = ContentNegotiation.Json;
        return JsonSerializer.SerializeAsync(c.Response.Body, new PeersEnvelopeDto(filtered, new PeersMetaDto(filtered.Count)), BeaconApiJson.Options, c.RequestAborted);
    }

    /// <summary><c>/eth/v1/node/peers/{peer_id}</c>: a single tracked peer, or 404 for an id this
    /// driver's peer manager (or the driver itself, when it has none) has no record of.</summary>
    private static Task PeerById(HttpContext c, string peer_id, BeaconApiContext ctx)
    {
        if (ContentNegotiation.Negotiate(c, sszSupported: false) is null)
        {
            return ContentNegotiation.WriteNotAcceptable(c);
        }

        if (ctx.PeerManager is null || !ctx.PeerManager.TryGetPeer(peer_id, out PeerRecord peer))
        {
            return ApiErrors.Write(c, StatusCodes.Status404NotFound, $"No known peer '{peer_id}'.", c.RequestAborted);
        }

        return BeaconApiJson.WriteDataAsync(c, ToPeerDto(peer), c.RequestAborted);
    }

    private static PeerDto ToPeerDto(PeerRecord peer) => new(
        peer.PeerId,
        peer.Enr,
        peer.LastKnownMultiaddr,
        StateWireName(peer.State),
        DirectionWireName(peer.Direction));

    /// <summary>Wire strings per the Beacon API's <c>node/peers</c> state set, mirroring
    /// <see cref="PeerConnectionState"/>'s own doc comment rather than a naming-policy guess.</summary>
    private static PeerConnectionState? ParseState(string? raw) => raw switch
    {
        "disconnected" => PeerConnectionState.Disconnected,
        "connecting" => PeerConnectionState.Connecting,
        "connected" => PeerConnectionState.Connected,
        "disconnecting" => PeerConnectionState.Disconnecting,
        _ => null,
    };

    private static PeerDirection? ParseDirection(string? raw) => raw switch
    {
        "inbound" => PeerDirection.Inbound,
        "outbound" => PeerDirection.Outbound,
        _ => null,
    };

    private static string StateWireName(PeerConnectionState state) => state switch
    {
        PeerConnectionState.Disconnected => "disconnected",
        PeerConnectionState.Connecting => "connecting",
        PeerConnectionState.Connected => "connected",
        PeerConnectionState.Disconnecting => "disconnecting",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static string DirectionWireName(PeerDirection direction) => direction switch
    {
        PeerDirection.Inbound => "inbound",
        PeerDirection.Outbound => "outbound",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
    };

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

    // No shared BeaconApiJson helper produces a {"data": [...], "meta": {...}} shape (only a single
    // "data" value or the fork-versioned envelope), so this is written directly rather than reusing one.
    private sealed record PeerDto(
        [property: JsonPropertyName("peer_id")] string PeerId,
        [property: JsonPropertyName("enr")] string? Enr,
        [property: JsonPropertyName("last_seen_p2p_address")] string LastSeenP2PAddress,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("direction")] string Direction);

    private sealed record PeersMetaDto([property: JsonPropertyName("count")] int Count);

    private sealed record PeersEnvelopeDto(
        [property: JsonPropertyName("data")] IReadOnlyList<PeerDto> Data,
        [property: JsonPropertyName("meta")] PeersMetaDto Meta);
}
