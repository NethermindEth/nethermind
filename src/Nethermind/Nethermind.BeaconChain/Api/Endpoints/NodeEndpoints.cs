// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Multiformats.Base;
using Multiformats.Hash;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary><c>/eth/v1/node/*</c>: health, identity, version, syncing, and peer accounting.</summary>
internal sealed class NodeEndpoints(BeaconApiContext ctx)
{
    /// <summary>Slots the head may lag the wall clock by and still count as caught up (health 200, is_syncing=false).</summary>
    private const long ReadySyncDistance = 1;

    [HttpGet("/eth/v1/node/health")]
    public IResult Health([FromQuery(Name = "syncing_status")] int? syncingStatus)
    {
        if (syncingStatus is < 100 or > 599)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Invalid syncing status code.");
        }

        StatusMessageV2 status = ctx.StatusSource.CurrentStatus;
        if (status.HeadRoot == Hash256.Zero)
        {
            // Nothing has been observed yet: neither "ready" nor "syncing" is true, only "not initialized".
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        long distance = (long)ctx.SlotClock.CurrentSlot - (long)status.HeadSlot;
        return Results.StatusCode(distance > ReadySyncDistance || !ctx.StatusSource.ExecutionInSync || !ctx.Engine.IsAvailable
            ? syncingStatus ?? StatusCodes.Status206PartialContent : StatusCodes.Status200OK);
    }

    [HttpGet("/eth/v1/node/version"), Negotiated]
    public static IResult Version() => ApiResults.Data(new VersionDto(BeaconP2P.ClientAgentVersion));

    [HttpGet("/eth/v1/node/identity"), Negotiated]
    public IResult Identity()
    {
        if (ctx.P2P?.LocalPeerId is not { } peerId)
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable,
                "The P2P host has not started yet; no peer id is assigned.");
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
            HexConvert.BitVectorToHex(ctx.MetadataSource.Current.Syncnets!),
            ctx.MetadataSource.Current.CustodyGroupCount.ToString());

        IdentityDto dto = new(
            peerIdString,
            ctx.Discovery?.LocalNodeRecord.ToString(),
            p2pAddresses,
            // Not independently derivable: this host does not compute a separate discovery-only
            // multiaddr set (see the report for why), so it is left honestly empty rather than
            // duplicating p2p_addresses under a different label.
            [],
            metadata);

        return ApiResults.Data(dto);
    }

    [HttpGet("/eth/v1/node/syncing"), Negotiated]
    public IResult Syncing()
    {
        StatusMessageV2 status = ctx.StatusSource.CurrentStatus;
        ulong wallSlot = ctx.SlotClock.CurrentSlot;
        long distance = (long)wallSlot - (long)status.HeadSlot;
        if (distance < 0) distance = 0;

        SyncingDto dto = new(
            status.HeadSlot.ToString(),
            distance.ToString(),
            distance > ReadySyncDistance,
            ResponseEnvelope.ExecutionOptimistic(ctx.StatusSource),
            !ctx.Engine.IsAvailable);

        return ApiResults.Data(dto);
    }

    [HttpGet("/eth/v1/node/peer_count"), Negotiated]
    public IResult PeerCount()
    {
        int disconnected = 0, connecting = 0, connected = 0, disconnecting = 0;
        foreach (PeerRecord peer in ctx.PeerManager?.Peers ?? [])
        {
            switch (peer.State)
            {
                case PeerConnectionState.Disconnected: disconnected++; break;
                case PeerConnectionState.Connecting: connecting++; break;
                case PeerConnectionState.Connected: connected++; break;
                case PeerConnectionState.Disconnecting: disconnecting++; break;
            }
        }
        PeerCountDto dto = new(disconnected.ToString(), connecting.ToString(), connected.ToString(), disconnecting.ToString());

        return ApiResults.Data(dto);
    }

    /// <summary>
    /// <c>/eth/v1/node/peers</c>: every peer this driver's peer manager currently tracks, optionally
    /// narrowed by the repeatable <c>state</c> and <c>direction</c> query parameters. A missing peer
    /// manager reports an empty list rather than an error - the same "zero rather than unavailable"
    /// choice <see cref="PeerCount"/> already makes for the same case.
    /// </summary>
    [HttpGet("/eth/v1/node/peers"), Negotiated]
    public IResult Peers([FromQuery(Name = "state")] string[] stateFilters, [FromQuery(Name = "direction")] string[] directionFilters)
    {
        List<PeerConnectionState> states = [];
        foreach (string raw in stateFilters)
        {
            if (ParseState(raw) is not { } state)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, $"Unknown peer state '{raw}'.");
            }

            states.Add(state);
        }

        List<PeerDirection> directions = [];
        foreach (string raw in directionFilters)
        {
            if (ParseDirection(raw) is not { } direction)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, $"Unknown peer direction '{raw}'.");
            }

            directions.Add(direction);
        }

        IReadOnlyList<PeerRecord> peers = ctx.PeerManager?.Peers ?? [];
        List<PeerDto> filtered = [with(peers.Count)];
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

        return ApiResults.Json(new PeersEnvelopeDto(filtered, new PeersMetaDto(filtered.Count)));
    }

    /// <summary><c>/eth/v1/node/peers/{peer_id}</c>: a single tracked peer, or 404 for an id this
    /// driver's peer manager (or the driver itself, when it has none) has no record of.</summary>
    [HttpGet("/eth/v1/node/peers/{peer_id}"), Negotiated]
    public IResult PeerById(string peer_id)
    {
        if (peer_id.Length > 64 || peer_id.AsSpan().IndexOfAnyExcept("123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz") >= 0
            || !Multihash.TryParse(peer_id, MultibaseEncoding.Base58Btc, out Multihash hash)
            || (int)hash.Code is not (0 or 18)
            || ((int)hash.Code == 18 ? hash.Length != 32 : hash.Length is < 1 or > 42)
            || !new PeerId(peer_id).Bytes.AsSpan().SequenceEqual(hash.ToBytes()))
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, $"Invalid peer ID: {peer_id}");
        }

        if (ctx.PeerManager is null || !ctx.PeerManager.TryGetPeer(peer_id, out PeerRecord peer))
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, $"No known peer '{peer_id}'.");
        }

        return ApiResults.Data(ToPeerDto(peer));
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
        [property: JsonPropertyName("syncnets")] string Syncnets,
        [property: JsonPropertyName("custody_group_count")] string CustodyGroupCount);

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
