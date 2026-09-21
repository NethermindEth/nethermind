// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// Maintains connections to the configured static peers, to peers discovered via discv5, and to
/// peers that connected to us: dials or admits them, exchanges <c>status</c> and <c>ping</c>
/// periodically, prunes peers on a fork digest mismatch or repeated failures, and keeps the
/// connected count within the configured peer band.
/// </summary>
/// <remarks>
/// The libp2p stack this plugin consumes has no gossipsub peer scoring at all, so this class is the
/// only line of defence against a misbehaving mesh neighbour: it cannot penalise a peer, only
/// disconnect it, cap how many it admits, and remember which ones kept faulting. See
/// <see cref="BanRecord"/> for the per-peer-id history that survives a single disconnect (the ban
/// list and diagnostics), as opposed to <see cref="ManagedPeer"/>, which only lives as long as the
/// session does.
/// </remarks>
public class PeerManager : IBeaconSyncPeerPool, IPeerDirectory
{
    // Generous: a slow peer hammered by range-sync batches can rack up transient timeouts
    // without being useless, and dialable mainnet peers are scarce.
    private const int MaxConsecutiveFailures = 8;
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);

    // Below MinPeerCount, maintenance (static-peer reconnect and health checks) runs on this
    // shorter cadence instead: the one lever PeerManager itself owns for "more aggressive" behaviour
    // while under-peered. Discovery's own candidate pacing is a different component's concern.
    private static readonly TimeSpan UnderPeeredMaintenanceInterval = TimeSpan.FromSeconds(5);

    // How often WaitForAdmissionCapacityAsync re-checks the target band while parked.
    private static readonly TimeSpan AdmissionPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);
    private const string PeerIdSeparator = "/p2p/";

    // Bounds the per-peer-id ban/diagnostics table so years of churn on a public network cannot
    // grow it forever. Real (deterministic) policy: evicts the oldest tracked non-banned entry -
    // see EvictIfOverCapacity. A banned entry is never evicted; the table can only exceed this bound
    // if every tracked id happens to be banned, which FaultDisconnectsBeforeBan makes rare.
    private const int MaxTrackedPeerIds = 8192;

    private readonly BeaconP2P _p2p;
    private readonly IBeaconChainConfig _config;
    private readonly IBeaconChainStatusSource _statusSource;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ManagedPeer> _peers = new();

    // Keyed by peer id (not dial address), so a ban and the message/failure history behind it
    // survive both a disconnect and a later reconnection attempt from a different address.
    private readonly ConcurrentDictionary<string, BanRecord> _peerRecords = new();
    private long _nextRecordSequence;

    // The one outbound-dial limiter for this plugin: both the static-peer loop and discovery-driven
    // dials fund through ConnectAsync and so through it, so it is where MaxConcurrentOutboundDials
    // is actually enforced, not a second copy of it.
    private readonly SemaphoreSlim _outboundDialGate;

    // Admission reservations by address: an entry is present from the moment any admission
    // (a static reconnect, a discovery dial, or a session the remote opened) is allowed to proceed
    // until its outcome is known, so a concurrent one cannot read a stale _peers.Count and admit past
    // MaxPeerCount (see TryReserveAdmissionSlot). Guarded by _admissionLock rather than left as
    // independent atomics, because the ceiling check and the reservation must happen as one step.
    private readonly ConcurrentDictionary<string, Reservation> _dialing = new();
    private readonly object _admissionLock = new();

    /// <summary>What an admission in flight already knows about its peer: enough for the directory's
    /// <see cref="PeerConnectionState.Connecting"/> entry and for the peer-id dedup in <see cref="IsKnown"/>.
    /// <paramref name="Enr"/> is only ever known for a discovery-sourced dial (see
    /// <see cref="TryAddPeerAsync"/>'s optional parameter); <c>null</c> for a static-peer reconnect or
    /// an inbound session, which have no ENR to offer.</summary>
    private readonly record struct Reservation(string PeerId, PeerDirection Direction, string? Enr);

    public PeerManager(BeaconP2P p2p, IBeaconChainConfig config, IBeaconChainStatusSource statusSource, ILogManager logManager)
    {
        _p2p = p2p;
        _config = config;
        _statusSource = statusSource;
        _logger = logManager.GetClassLogger<PeerManager>();
        _outboundDialGate = new SemaphoreSlim(Math.Max(1, config.MaxConcurrentOutboundDials));

        // A session the remote side opened has no dial here to admit it through; this is its only way in.
        p2p.SessionEstablished += OnSessionEstablished;
    }

    /// <summary>Raised with the dropped peer's id so dial dedup can allow a later re-dial.</summary>
    public event Action<string>? PeerDropped;

    /// <summary>The number of connected, status-exchanged peers.</summary>
    public int PeerCount => _peers.Count;

    public async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceRoundAsync(token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (_logger.IsError) _logger.Error("Beacon chain peer maintenance failed.", e);
            }

            await Task.Delay(NextMaintenanceInterval, token);
        }
    }

    /// <summary>The low watermark's one real effect: maintenance (static-peer reconnect, health
    /// checks) runs more often while under-peered instead of the knob being read nowhere.</summary>
    private TimeSpan NextMaintenanceInterval => _peers.Count < _config.MinPeerCount ? UnderPeeredMaintenanceInterval : MaintenanceInterval;

    /// <summary>Internal so a test can assert the cadence choice without waiting out a real interval.</summary>
    internal TimeSpan NextMaintenanceIntervalForTest => NextMaintenanceInterval;

    /// <summary>
    /// Backpressure for the discovery dial loop: the loop should ask whether there is room rather
    /// than deciding for itself from raw config, so this is the one place "at target" is defined.
    /// Returns once the pool is below <see cref="IBeaconChainConfig.TargetPeerCount"/> counting both
    /// connected peers and dials already admitted but not yet resolved, so a burst of concurrent
    /// dials cannot itself blow through the target the moment they all land.
    /// </summary>
    public async Task WaitForAdmissionCapacityAsync(CancellationToken token)
    {
        while (_peers.Count + _dialing.Count >= _config.TargetPeerCount)
        {
            await Task.Delay(AdmissionPollInterval, token);
        }
    }

    /// <summary>
    /// Connects missing static peers, re-exchanges status/ping with connected ones (pruning unhealthy
    /// peers), then trims back to the peer band's high watermark if maintenance left it over.
    /// </summary>
    public async Task RunMaintenanceRoundAsync(CancellationToken token)
    {
        string[] staticAddresses = StaticPeerAddresses();
        foreach (string address in staticAddresses)
        {
            if (!IsConnected(address))
            {
                await ConnectAsync(address, token);
            }
        }

        foreach (KeyValuePair<string, ManagedPeer> peer in _peers)
        {
            await CheckHealthAsync(peer.Value, token);
        }

        await TrimToPeerBandAsync(staticAddresses, token);
    }

    /// <summary>
    /// Drops the worst non-static peers down to <see cref="IBeaconChainConfig.TargetPeerCount"/> once
    /// the connected count is over <see cref="IBeaconChainConfig.MaxPeerCount"/> (the high watermark).
    /// </summary>
    /// <remarks>
    /// Configured static peers are never trimmed: dropping one an operator explicitly asked for would
    /// just have the next maintenance round reconnect it, and it is not a "worst" peer by any measure
    /// here. Worst is ranked by consecutive health-check failures first, then by stale head slot -
    /// the same signals the health check itself already trusts, not a new scoring scheme.
    /// </remarks>
    private async Task TrimToPeerBandAsync(string[] staticAddresses, CancellationToken token)
    {
        int overflow = _peers.Count - _config.MaxPeerCount;
        if (overflow <= 0)
        {
            return;
        }

        HashSet<string> exempt = new(staticAddresses, StringComparer.Ordinal);
        List<ManagedPeer> trimmable = [];
        foreach (KeyValuePair<string, ManagedPeer> peer in _peers)
        {
            if (!exempt.Contains(peer.Key))
            {
                trimmable.Add(peer.Value);
            }
        }

        trimmable.Sort(static (a, b) =>
        {
            int byFailures = b.ConsecutiveFailures.CompareTo(a.ConsecutiveFailures);
            return byFailures != 0 ? byFailures : a.HeadSlot.CompareTo(b.HeadSlot);
        });

        int target = Math.Min(_config.TargetPeerCount, _config.MaxPeerCount);
        int toDrop = Math.Min(trimmable.Count, _peers.Count - target);
        for (int i = 0; i < toDrop; i++)
        {
            await DropAsync(trimmable[i], GoodbyeReason.TooManyPeers, "over the configured peer band", token);
        }
    }

    public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot)
    {
        List<ManagedPeer> best = [];
        foreach (KeyValuePair<string, ManagedPeer> peer in _peers)
        {
            if (peer.Value.Status is not null && peer.Value.HeadSlot >= minHeadSlot)
            {
                best.Add(peer.Value);
            }
        }

        best.Sort(static (a, b) => b.HeadSlot.CompareTo(a.HeadSlot));
        return best;
    }

    /// <summary>
    /// Dials a discovered peer (bounded by <see cref="DialTimeout"/>) and adds it to the pool when
    /// the status exchange succeeds. Refuses a banned peer id or one that would push the pool past
    /// <see cref="IBeaconChainConfig.MaxPeerCount"/> without attempting a dial.
    /// </summary>
    /// <param name="address">The dial multiaddr, including its <c>/p2p/</c> peer-id component.</param>
    /// <param name="token">Cancels the dial; a timeout past <see cref="DialTimeout"/> is treated as a refusal, not propagated.</param>
    /// <param name="enr">The candidate's discv5 ENR text when known, so the resulting <see cref="PeerRecord"/>
    /// can report it truthfully instead of <c>null</c>. Omitted for a static-peer reconnect, which has no ENR to offer.</param>
    /// <returns><c>true</c> when the peer is (already) connected and on our fork.</returns>
    public async Task<bool> TryAddPeerAsync(string address, CancellationToken token, string? enr = null)
    {
        if (IsConnected(address))
        {
            return true;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(DialTimeout);
        try
        {
            return await ConnectAsync(address, cts.Token, enr);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug($"Dialing beacon chain peer {address} timed out");
            return false;
        }
    }

    /// <summary>
    /// Atomically checks the ceiling and reserves <paramref name="address"/> as one step, so
    /// concurrent admissions cannot all observe the same stale count and all pass (the exact
    /// overshoot this closes: up to MaxConcurrentOutboundDials could previously admit past
    /// MaxPeerCount). Also refuses a peer id already connected or in flight under any address, so
    /// one session can never be recorded twice; <paramref name="atCeiling"/> tells the two refusals
    /// apart, because only the ceiling one may cost the remote its session.
    /// </summary>
    private bool TryReserveAdmissionSlot(string address, string peerId, PeerDirection direction, string? enr, out bool atCeiling)
    {
        lock (_admissionLock)
        {
            if (_dialing.ContainsKey(address) || IsKnown(peerId))
            {
                atCeiling = false;
                return false;
            }

            atCeiling = _peers.Count + _dialing.Count >= _config.MaxPeerCount;
            if (atCeiling)
            {
                return false;
            }

            _dialing[address] = new Reservation(peerId, direction, enr);
            return true;
        }
    }

    /// <summary>Connected under <paramref name="address"/>, or under any other address as the same peer id.</summary>
    private bool IsConnected(string address) => _peers.ContainsKey(address) || TryFindConnected(ExtractPeerId(address), out _);

    /// <summary>Connected or in flight, by peer id: the identity check behind every admission path's dedup.</summary>
    private bool IsKnown(string peerId)
    {
        if (TryFindConnected(peerId, out _))
        {
            return true;
        }

        foreach (KeyValuePair<string, Reservation> dialing in _dialing)
        {
            if (string.Equals(dialing.Value.PeerId, peerId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Already in the pool as this exact session or as the same established peer id under any address.</summary>
    private bool IsRecorded(ISession session, string establishedId)
    {
        foreach (KeyValuePair<string, ManagedPeer> connected in _peers)
        {
            if (ReferenceEquals(connected.Value.Session, session) || string.Equals(connected.Value.PeerId, establishedId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryFindConnected(string peerId, out ManagedPeer? peer)
    {
        foreach (KeyValuePair<string, ManagedPeer> connected in _peers)
        {
            if (string.Equals(connected.Value.PeerId, peerId, StringComparison.Ordinal))
            {
                peer = connected.Value;
                return true;
            }
        }

        peer = null;
        return false;
    }

    /// <summary>A point-in-time snapshot of what this plugin can observe about one peer id, for diagnostics.</summary>
    /// <remarks>
    /// This is the "instrumentation of what cannot be defended" this plugin can offer in place of
    /// gossipsub scoring: message counts, peer-reported failures (<see cref="IBeaconSyncPeer.ReportFailure"/>,
    /// which covers both outright request failures and content validation failures such as a bad
    /// parent root - this plugin has no finer-grained signal than that to report), and disconnect
    /// history, keyed by peer id so it survives the peer disconnecting.
    /// </remarks>
    public readonly record struct PeerDiagnostics(
        string PeerId,
        bool Connected,
        ulong HeadSlot,
        int ConsecutiveFailures,
        long MessagesSent,
        long FailuresReported,
        int DisconnectCount,
        string? LastDisconnectReason,
        string? LastDisconnectDetail,
        bool Banned);

    /// <summary>Snapshots every peer id this plugin has ever connected to and still remembers.</summary>
    public IReadOnlyList<PeerDiagnostics> GetPeerDiagnostics()
    {
        Dictionary<string, ManagedPeer> connectedByPeerId = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ManagedPeer> peer in _peers)
        {
            connectedByPeerId[peer.Value.PeerId] = peer.Value;
        }

        List<PeerDiagnostics> snapshot = new(_peerRecords.Count);
        foreach (KeyValuePair<string, BanRecord> record in _peerRecords)
        {
            bool connected = connectedByPeerId.TryGetValue(record.Key, out ManagedPeer? peer);
            snapshot.Add(new PeerDiagnostics(
                record.Key,
                connected,
                connected ? peer!.HeadSlot : 0,
                connected ? peer!.ConsecutiveFailures : 0,
                connected ? peer!.MessagesSent : record.Value.MessagesSent,
                connected ? peer!.FailuresReported : record.Value.FailuresReported,
                record.Value.DisconnectCount,
                record.Value.LastDisconnectReason,
                record.Value.LastDisconnectDetail,
                record.Value.Banned));
        }

        return snapshot;
    }

    /// <summary>The Beacon API's <c>node/peers</c> surface. An admission in flight (reserved but not
    /// yet resolved - see <see cref="TryReserveAdmissionSlot"/>) reports as
    /// <see cref="PeerConnectionState.Connecting"/>; everything in <c>_peers</c> is already
    /// status-exchanged and reports as <see cref="PeerConnectionState.Connected"/> with the direction
    /// the libp2p layer recorded for its session (<see cref="BeaconP2P.SessionInfo"/>), so a peer that
    /// connected to us is <see cref="PeerDirection.Inbound"/>. <see cref="PeerConnectionState.Disconnected"/>
    /// and <see cref="PeerConnectionState.Disconnecting"/> are never produced: a dropped peer is simply
    /// forgotten here (its history lives in <see cref="BanRecord"/>). <c>AgentVersion</c> is the identify
    /// agent string the libp2p layer captured, <c>null</c> only when that probe went unanswered.
    /// <c>Enr</c> is the discv5 ENR text supplied to <see cref="TryAddPeerAsync"/> for a peer this
    /// manager discovered and dialed itself; <c>null</c> for a static peer or one that connected to us,
    /// neither of which offers an ENR at admission time.</summary>
    public IReadOnlyList<PeerRecord> Peers
    {
        get
        {
            List<PeerRecord> result = new(_peers.Count + _dialing.Count);
            foreach (KeyValuePair<string, ManagedPeer> peer in _peers)
            {
                result.Add(ToConnectedRecord(peer.Value));
            }

            foreach (KeyValuePair<string, Reservation> dialing in _dialing)
            {
                if (!_peers.ContainsKey(dialing.Key))
                {
                    result.Add(ToConnectingRecord(dialing.Key, dialing.Value));
                }
            }

            return result;
        }
    }

    /// <summary>Looks up one peer by libp2p peer id (not dial address). An empty id or one this
    /// manager has no record of is refused rather than matched against an unrelated entry - see the
    /// class remarks on never handing out on an unresolved identity.</summary>
    public bool TryGetPeer(string peerId, out PeerRecord peer)
    {
        if (string.IsNullOrEmpty(peerId))
        {
            peer = default;
            return false;
        }

        if (TryFindConnected(peerId, out ManagedPeer? connected))
        {
            peer = ToConnectedRecord(connected!);
            return true;
        }

        foreach (KeyValuePair<string, Reservation> dialing in _dialing)
        {
            if (!_peers.ContainsKey(dialing.Key) && string.Equals(dialing.Value.PeerId, peerId, StringComparison.Ordinal))
            {
                peer = ToConnectingRecord(dialing.Key, dialing.Value);
                return true;
            }
        }

        peer = default;
        return false;
    }

    private static PeerRecord ToConnectedRecord(ManagedPeer peer) => new(
        peer.PeerId,
        peer.Direction,
        PeerConnectionState.Connected,
        peer.Session.RemoteAddress?.ToString() ?? peer.Id,
        peer.AgentVersion,
        peer.Enr);

    private static PeerRecord ToConnectingRecord(string address, Reservation reservation) => new(
        reservation.PeerId,
        reservation.Direction,
        PeerConnectionState.Connecting,
        address,
        AgentVersion: null,
        reservation.Enr);

    // GoodbyeReason is const ulong, not an enum, so the wire value is resolved to a name by hand
    // for a bounded-cardinality metric label instead of the raw number.
    private static string GoodbyeReasonName(ulong reason) => reason switch
    {
        GoodbyeReason.ClientShutdown => "ClientShutdown",
        GoodbyeReason.IrrelevantNetwork => "IrrelevantNetwork",
        GoodbyeReason.Fault => "Fault",
        GoodbyeReason.TooManyPeers => "TooManyPeers",
        GoodbyeReason.Banned => "Banned",
        _ => "Other",
    };

    private string[] StaticPeerAddresses() =>
        _config.StaticPeers?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    /// <summary>The stable part of a dial address to key the ban list and diagnostics on: the libp2p
    /// peer id after the last <c>/p2p/</c> component, or the whole address when it has none.</summary>
    private static string ExtractPeerId(string address)
    {
        int index = address.LastIndexOf(PeerIdSeparator, StringComparison.Ordinal);
        return index < 0 ? address : address[(index + PeerIdSeparator.Length)..];
    }

    /// <summary>Internal so a test can check the ban key derivation directly.</summary>
    internal static string ExtractPeerIdForTest(string address) => ExtractPeerId(address);

    private bool IsBanned(string peerId) => _peerRecords.TryGetValue(peerId, out BanRecord? record) && record.Banned;

    private BanRecord GetOrCreateRecord(string peerId)
    {
        if (_peerRecords.TryGetValue(peerId, out BanRecord? existing))
        {
            return existing;
        }

        EvictIfOverCapacity();
        return _peerRecords.GetOrAdd(peerId, _ => new BanRecord(Interlocked.Increment(ref _nextRecordSequence)));
    }

    /// <summary>Real (deterministic) bound on the ban/diagnostics table: evicts the oldest tracked
    /// non-banned entry by creation order, not an arbitrary one from undefined dictionary enumeration
    /// order. A banned entry is never evicted, so the table can still exceed the cap if every tracked
    /// id happens to be banned - accepted, since that needs FaultDisconnectsBeforeBan-many faults per
    /// id and is not the churn this bound defends against.</summary>
    private void EvictIfOverCapacity()
    {
        if (_peerRecords.Count < MaxTrackedPeerIds)
        {
            return;
        }

        string? oldestKey = null;
        long oldestSequence = long.MaxValue;
        foreach (KeyValuePair<string, BanRecord> entry in _peerRecords)
        {
            if (!entry.Value.Banned && entry.Value.Sequence < oldestSequence)
            {
                oldestSequence = entry.Value.Sequence;
                oldestKey = entry.Key;
            }
        }

        if (oldestKey is not null)
        {
            _peerRecords.TryRemove(oldestKey, out _);
        }
    }

    /// <summary>
    /// The one outbound admission path: the static-peer reconnect loop and discovery dials both come
    /// through here, so the ban check and the ceiling reservation live here and not in one caller
    /// (the static path used to skip the reservation and could take the pool past MaxPeerCount).
    /// Refuses a banned id, a peer already connected or in flight, or one that would overshoot the
    /// ceiling, all without attempting a dial.
    /// </summary>
    private async Task<bool> ConnectAsync(string address, CancellationToken token, string? enr = null)
    {
        string peerId = ExtractPeerId(address);
        if (IsBanned(peerId))
        {
            if (_logger.IsDebug) _logger.Debug($"Refusing to dial banned beacon chain peer {peerId}");
            return false;
        }

        if (!TryReserveAdmissionSlot(address, peerId, PeerDirection.Outbound, enr, out bool atCeiling))
        {
            if (_logger.IsDebug) _logger.Debug($"Refusing to dial {address}: {(atCeiling ? $"at the configured peer band ceiling ({_config.MaxPeerCount})" : "already connected or in flight")}");
            return false;
        }

        try
        {
            await _outboundDialGate.WaitAsync(token);
            try
            {
                ISession session = await _p2p.DialPeerAsync(Multiaddress.Decode(address), token);
                // The dial returns before the agent probe has answered, and may hand back a session that
                // already existed (the peer connected to us first): wait for what the libp2p layer
                // recorded instead of assuming "we dialed it, no client string".
                BeaconP2P.SessionInfo info = await _p2p.GetSessionInfoAsync(session, token);
                return await AdmitSessionAsync(address, peerId, session, info, enr, token);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug($"Failed to connect to beacon chain peer {address}: {e.Message}");
                return false;
            }
            finally
            {
                _outboundDialGate.Release();
            }
        }
        finally
        {
            _dialing.TryRemove(address, out _);
        }
    }

    /// <summary>Status-exchanges an established session and, when it is on our fork, records it under
    /// <paramref name="address"/>. Shared by every admission path; the caller holds the reservation.</summary>
    private async Task<bool> AdmitSessionAsync(string address, string peerId, ISession session, BeaconP2P.SessionInfo info, string? enr, CancellationToken token)
    {
        // A dial address without a /p2p/ component keys on the whole address, so the session's real
        // peer id is the only reliable way to notice it is already admitted under another address.
        string establishedId = BeaconP2P.RemotePeerIdOf(session)?.ToString() ?? peerId;
        if (IsRecorded(session, establishedId))
        {
            return true;
        }

        ManagedPeer peer = new(this, _p2p, address, peerId, session, info.Direction, info.AgentVersion, enr);
        if (!await UpdateStatusAsync(peer, token))
        {
            return false;
        }

        lock (_admissionLock)
        {
            // A dial address without /p2p/ cannot be matched by id to the remote-opened session until
            // now, so two admissions can hold the same session and both pass the check above: one
            // session, one entry, decided under the same lock the ceiling reads _peers.Count under.
            if (IsRecorded(session, establishedId))
            {
                return true;
            }

            _peers[address] = peer;
        }

        Metrics.BeaconChainPeersConnected++;
        Metrics.BeaconChainPeerCount = _peers.Count;
        if (_logger.IsInfo) _logger.Info($"Connected to beacon chain peer {address} ({info.Direction.ToString().ToLowerInvariant()}, head slot {peer.HeadSlot})");
        return true;
    }

    private void OnSessionEstablished(ISession session, BeaconP2P.SessionInfo info)
    {
        // A session this manager is dialing itself is admitted by that dial once it returns; only a
        // session nobody here asked for (the remote connected to us) is admitted from the event.
        string address = session.RemoteAddress.ToString();
        string peerId = BeaconP2P.RemotePeerIdOf(session)?.ToString() ?? ExtractPeerId(address);
        if (IsKnown(peerId))
        {
            return;
        }

        _ = AdmitUnclaimedSessionAsync(session, address, peerId, info);
    }

    /// <summary>
    /// The inbound admission path: the same ban check and ceiling reservation as a dial, but with the
    /// session already open, so a refusal has to actively send <c>goodbye</c> and disconnect rather
    /// than just not dial. A duplicate (the dial path claimed the same peer id meanwhile) is left alone:
    /// that is the very session the dial is about to record.
    /// </summary>
    private async Task AdmitUnclaimedSessionAsync(ISession session, string address, string peerId, BeaconP2P.SessionInfo info)
    {
        try
        {
            if (IsBanned(peerId))
            {
                await RefuseSessionAsync(session, peerId, GoodbyeReason.Banned, $"banned peer {peerId} connected to us");
                return;
            }

            if (!TryReserveAdmissionSlot(address, peerId, info.Direction, null, out bool atCeiling))
            {
                if (atCeiling)
                {
                    await RefuseSessionAsync(session, peerId, GoodbyeReason.TooManyPeers, $"{address} connected to us at the configured peer band ceiling ({_config.MaxPeerCount})");
                }

                return;
            }

            try
            {
                // The remote opened this session; we never discovered it via discv5 ourselves, so there
                // is no ENR to attribute to it here (see the Enr param note on TryAddPeerAsync).
                await AdmitSessionAsync(address, peerId, session, info, null, CancellationToken.None);
            }
            finally
            {
                _dialing.TryRemove(address, out _);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Admitting beacon chain peer {address} failed: {e.Message}");
        }
    }

    /// <summary>Sends <c>goodbye</c> and disconnects a session that was never admitted. Recorded in the
    /// peer id's disconnect history like any other drop, so who keeps knocking while we are full or
    /// after a ban is visible in the diagnostics rather than only in a debug log.</summary>
    private async Task RefuseSessionAsync(ISession session, string peerId, ulong reason, string detail)
    {
        if (_logger.IsDebug) _logger.Debug($"Refusing beacon chain peer: {detail}");
        RecordDisconnect(peerId, messagesSent: 0, failuresReported: 0, reason, detail);
        await _p2p.GoodbyeAsync(session, reason, CancellationToken.None);
        try
        {
            await session.DisconnectAsync();
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Disconnect from {session.RemoteAddress} failed: {e.Message}");
        }
    }

    private async Task CheckHealthAsync(ManagedPeer peer, CancellationToken token)
    {
        try
        {
            if (!await UpdateStatusAsync(peer, token))
            {
                return;
            }

            await _p2p.PingAsync(peer.Session, token);
            peer.RecordMessageSent();
            peer.ConsecutiveFailures = 0;
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            peer.ConsecutiveFailures++;
            if (_logger.IsDebug) _logger.Debug($"Beacon chain peer {peer.Id} failed health check ({peer.ConsecutiveFailures}/{MaxConsecutiveFailures}): {e.Message}");
            if (peer.ConsecutiveFailures >= MaxConsecutiveFailures)
            {
                await DropAsync(peer, GoodbyeReason.Fault, "repeated failures", token);
            }
        }
    }

    /// <returns><c>false</c> when the peer was dropped for being on a different fork.</returns>
    private async Task<bool> UpdateStatusAsync(ManagedPeer peer, CancellationToken token)
    {
        StatusMessageV2 status = await _p2p.RequestStatusAsync(peer.Session, token);
        peer.RecordMessageSent();
        if (!status.ForkDigest.AsSpan().SequenceEqual(_statusSource.CurrentStatus.ForkDigest))
        {
            await DropAsync(peer, GoodbyeReason.IrrelevantNetwork, "fork digest mismatch", token);
            return false;
        }

        peer.Status = status;
        return true;
    }

    private async Task DropAsync(ManagedPeer peer, ulong reason, string detail, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"Dropping beacon chain peer {peer.Id}: {detail}");
        _peers.TryRemove(peer.Id, out _);
        Metrics.BeaconChainPeersDropped++;
        Metrics.BeaconChainPeersDroppedByReason.Increment(new StringLabel(GoodbyeReasonName(reason)));
        Metrics.BeaconChainPeerCount = _peers.Count;
        RecordDisconnect(peer, reason, detail);
        PeerDropped?.Invoke(peer.Id);
        await _p2p.GoodbyeAsync(peer.Session, reason, token);
        try
        {
            await peer.Session.DisconnectAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Disconnect from {peer.Id} failed: {e.Message}");
        }
    }

    /// <summary>
    /// Folds a dropped session's counters into its peer id's durable record, and bans the id once it
    /// has caused <see cref="IBeaconChainConfig.FaultDisconnectsBeforeBan"/> consecutive fault
    /// disconnects. A non-fault disconnect (fork rotation, our own shutdown) resets that streak: it
    /// is not evidence of misbehaviour, and BPO digest rotations legitimately cause fork-mismatch
    /// drops of otherwise-healthy peers.
    /// </summary>
    private void RecordDisconnect(ManagedPeer peer, ulong reason, string detail) =>
        RecordDisconnect(peer.PeerId, peer.MessagesSent, peer.FailuresReported, reason, detail);

    /// <summary>
    /// The session-independent half of <see cref="DropAsync"/>'s bookkeeping. Internal so a test can
    /// drive the ban/diagnostics state machine directly against real peer ids without standing up a
    /// live libp2p session for every one of <see cref="IBeaconChainConfig.FaultDisconnectsBeforeBan"/>
    /// fault disconnects.
    /// </summary>
    internal void RecordDisconnect(string peerId, long messagesSent, long failuresReported, ulong reason, string detail)
    {
        BanRecord record = GetOrCreateRecord(peerId);
        record.LastDisconnectReason = GoodbyeReasonName(reason);
        record.LastDisconnectDetail = detail;
        record.MessagesSent = messagesSent;
        record.FailuresReported = failuresReported;
        Interlocked.Increment(ref record.DisconnectCount);

        if (reason != GoodbyeReason.Fault)
        {
            Volatile.Write(ref record.ConsecutiveFaultDisconnects, 0);
            return;
        }

        int consecutiveFaults = Interlocked.Increment(ref record.ConsecutiveFaultDisconnects);
        if (consecutiveFaults >= _config.FaultDisconnectsBeforeBan && !record.Banned)
        {
            record.Banned = true;
            if (_logger.IsWarn) _logger.Warn($"Banned beacon chain peer {peerId} after {consecutiveFaults} consecutive fault disconnects");
        }
    }

    /// <summary>Internal so a test can assert ban state without dialing: see <see cref="RecordDisconnect(string,long,long,ulong,string)"/>.</summary>
    internal bool IsBannedForTest(string peerId) => IsBanned(peerId);

    /// <summary>Internal so a test can put an address straight into the "dialing" reservation set,
    /// to exercise <see cref="TryGetPeer"/>'s own guard without racing a real dial's transient window.
    /// <paramref name="enr"/> lets a test also exercise the Beacon API's <c>enr</c> field without a live dial.</summary>
    internal void ReserveDialingForTest(string address, string? enr = null) => _dialing[address] = new Reservation(ExtractPeerId(address), PeerDirection.Outbound, enr);

    /// <summary>The durable, peer-id-keyed half of a peer's history: outlives any one
    /// <see cref="ManagedPeer"/> session so a ban and disconnect history survive reconnection attempts.
    /// Named apart from the public, Beacon-API-shaped <see cref="PeerRecord"/> struct, which this is
    /// not: that one is a live-connection snapshot, this is durable ban/diagnostics bookkeeping.</summary>
    private sealed class BanRecord(long sequence)
    {
        /// <summary>Creation order, for a deterministic oldest-first eviction in <see cref="EvictIfOverCapacity"/>.</summary>
        public readonly long Sequence = sequence;
        public volatile bool Banned;
        public int ConsecutiveFaultDisconnects;
        public int DisconnectCount;
        public long MessagesSent;
        public long FailuresReported;
        public string? LastDisconnectReason;
        public string? LastDisconnectDetail;
    }

    private sealed class ManagedPeer(PeerManager manager, BeaconP2P p2p, string address, string peerId, ISession session, PeerDirection direction, string? agentVersion, string? enr) : IBeaconSyncPeer
    {
        private int _consecutiveFailures;
        private long _messagesSent;
        private long _failuresReported;

        public ISession Session { get; } = session;
        public StatusMessageV2? Status { get; set; }

        /// <summary>The libp2p peer id this session was dialed as (see <see cref="PeerManager.ExtractPeerId"/>).</summary>
        public string PeerId { get; } = peerId;

        /// <summary>Which side opened the session, as the libp2p layer saw it happen.</summary>
        public PeerDirection Direction { get; } = direction;

        /// <summary>The identify agent string, <c>null</c> when the peer left the probe unanswered.</summary>
        public string? AgentVersion { get; } = agentVersion;

        /// <summary>The discv5 ENR text this peer was discovered with; <c>null</c> for a static peer or
        /// an inbound session (see <see cref="PeerManager.TryAddPeerAsync"/>'s optional parameter).</summary>
        public string? Enr { get; } = enr;

        public int ConsecutiveFailures
        {
            get => _consecutiveFailures;
            set => _consecutiveFailures = value;
        }

        public long MessagesSent => Interlocked.Read(ref _messagesSent);
        public long FailuresReported => Interlocked.Read(ref _failuresReported);

        public string Id => address;
        public ulong HeadSlot => Status?.HeadSlot ?? 0;

        public void RecordMessageSent() => Interlocked.Increment(ref _messagesSent);

        public async Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token)
        {
            RecordMessageSent();
            return await p2p.RequestBlocksByRangeAsync(Session, startSlot, count, token);
        }

        public async Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token)
        {
            RecordMessageSent();
            return await p2p.RequestBlocksByRootAsync(Session, roots, token);
        }

        public async Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token)
        {
            RecordMessageSent();
            return await p2p.RequestDataColumnSidecarsByRangeAsync(Session, startSlot, count, columns, token);
        }

        public void ReportFailure(PeerFailureReason reason, string? detail = null)
        {
            Interlocked.Increment(ref _failuresReported);

            // A dead session means every further request would fail, so skip the failure budget and
            // let the next maintenance round (or the dial-loop cooldown) reconnect instead of
            // wedging on a zombie session.
            int failures = reason == PeerFailureReason.SessionClosed
                ? Interlocked.Exchange(ref _consecutiveFailures, MaxConsecutiveFailures)
                : Interlocked.Increment(ref _consecutiveFailures);
            Metrics.BeaconChainPeerFailuresByReason.Increment(new StringLabel(reason.ToString()));
            if (manager._logger.IsDebug) manager._logger.Debug($"Beacon chain peer {Id} reported as failing ({failures}/{MaxConsecutiveFailures}): {reason}{(detail is null ? "" : $" ({detail})")}");
        }

        /// <summary>Legacy free-text overload for callers this change's file boundary could not
        /// reach (RangeSync.cs). Classifies by the exact substring rule this method used before the
        /// reason became closed-cardinality, so the metric label and the fatal-session fast path both
        /// keep behaving the same for those callers.</summary>
        public void ReportFailure(string reason)
        {
            bool sessionDead = reason.Contains("Channel closed", StringComparison.OrdinalIgnoreCase) || reason.Contains("session", StringComparison.OrdinalIgnoreCase);
            ReportFailure(sessionDead ? PeerFailureReason.SessionClosed : PeerFailureReason.RequestFailed, reason);
        }
    }
}
