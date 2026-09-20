// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
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
/// Maintains connections to the configured static peers and to peers discovered via discv5: dials
/// them, exchanges <c>status</c> and <c>ping</c> periodically, prunes peers on a fork digest
/// mismatch or repeated failures, and keeps the connected count within the configured peer band.
/// </summary>
/// <remarks>
/// The libp2p stack this plugin consumes has no gossipsub peer scoring at all, so this class is the
/// only line of defence against a misbehaving mesh neighbour: it cannot penalise a peer, only
/// disconnect it, cap how many it admits, and remember which ones kept faulting. See
/// <see cref="PeerRecord"/> for the per-peer-id history that survives a single disconnect (the ban
/// list and diagnostics), as opposed to <see cref="ManagedPeer"/>, which only lives as long as the
/// session does.
/// </remarks>
public class PeerManager(
    BeaconP2P p2p,
    IBeaconChainConfig config,
    IBeaconChainStatusSource statusSource,
    ILogManager logManager) : IBeaconSyncPeerPool
{
    // Generous: a slow peer hammered by range-sync batches can rack up transient timeouts
    // without being useless, and dialable mainnet peers are scarce.
    private const int MaxConsecutiveFailures = 8;
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);
    private const string PeerIdSeparator = "/p2p/";

    // Bounds the per-peer-id ban/diagnostics table so years of churn on a public network cannot
    // grow it forever. Best-effort eviction, not a true LRU: see EvictIfOverCapacity.
    private const int MaxTrackedPeerIds = 8192;

    private readonly ILogger _logger = logManager.GetClassLogger<PeerManager>();
    private readonly ConcurrentDictionary<string, ManagedPeer> _peers = new();

    // Keyed by peer id (not dial address), so a ban and the message/failure history behind it
    // survive both a disconnect and a later reconnection attempt from a different address.
    private readonly ConcurrentDictionary<string, PeerRecord> _peerRecords = new();

    // The one outbound-dial limiter for this plugin: both the static-peer loop below and
    // discovery-driven dials from TryAddPeerAsync fund through it, so it is where
    // MaxConcurrentOutboundDials is actually enforced, not a second copy of it.
    private readonly SemaphoreSlim _outboundDialGate = new(Math.Max(1, config.MaxConcurrentOutboundDials));

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

            await Task.Delay(MaintenanceInterval, token);
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
            if (!_peers.ContainsKey(address))
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
        int overflow = _peers.Count - config.MaxPeerCount;
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

        int target = Math.Min(config.TargetPeerCount, config.MaxPeerCount);
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
    /// <returns><c>true</c> when the peer is (already) connected and on our fork.</returns>
    public async Task<bool> TryAddPeerAsync(string address, CancellationToken token)
    {
        if (_peers.ContainsKey(address))
        {
            return true;
        }

        // The dial address is always a concrete string here (never an unresolved/absent identity):
        // ExtractPeerId falls back to the whole address when it has no /p2p/ component, so the ban
        // and admission checks below always have something concrete to key on.
        string peerId = ExtractPeerId(address);
        if (IsBanned(peerId))
        {
            if (_logger.IsDebug) _logger.Debug($"Refusing to dial banned beacon chain peer {peerId}");
            return false;
        }

        if (_peers.Count >= config.MaxPeerCount)
        {
            if (_logger.IsDebug) _logger.Debug($"Refusing to dial {address}: at the configured peer band ceiling ({config.MaxPeerCount})");
            return false;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(DialTimeout);
        try
        {
            return await ConnectAsync(address, cts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug($"Dialing beacon chain peer {address} timed out");
            return false;
        }
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
        foreach (KeyValuePair<string, PeerRecord> record in _peerRecords)
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
        config.StaticPeers?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    /// <summary>The stable part of a dial address to key the ban list and diagnostics on: the libp2p
    /// peer id after the last <c>/p2p/</c> component, or the whole address when it has none.</summary>
    private static string ExtractPeerId(string address)
    {
        int index = address.LastIndexOf(PeerIdSeparator, StringComparison.Ordinal);
        return index < 0 ? address : address[(index + PeerIdSeparator.Length)..];
    }

    /// <summary>Internal so a test can check the ban key derivation directly.</summary>
    internal static string ExtractPeerIdForTest(string address) => ExtractPeerId(address);

    private bool IsBanned(string peerId) => _peerRecords.TryGetValue(peerId, out PeerRecord? record) && record.Banned;

    private PeerRecord GetOrCreateRecord(string peerId)
    {
        if (_peerRecords.TryGetValue(peerId, out PeerRecord? existing))
        {
            return existing;
        }

        EvictIfOverCapacity();
        return _peerRecords.GetOrAdd(peerId, static _ => new PeerRecord());
    }

    /// <summary>Best-effort bound on the ban/diagnostics table, not a true LRU: evicts arbitrary
    /// non-banned entries so the table cannot grow without limit over a long-running node's peer churn.</summary>
    private void EvictIfOverCapacity()
    {
        if (_peerRecords.Count < MaxTrackedPeerIds)
        {
            return;
        }

        foreach (KeyValuePair<string, PeerRecord> entry in _peerRecords)
        {
            if (!entry.Value.Banned && _peerRecords.TryRemove(entry.Key, out _) && _peerRecords.Count < MaxTrackedPeerIds)
            {
                return;
            }
        }
    }

    private async Task<bool> ConnectAsync(string address, CancellationToken token)
    {
        string peerId = ExtractPeerId(address);
        if (IsBanned(peerId))
        {
            // Reached from the static-peer reconnect loop, which does not go through
            // TryAddPeerAsync's own ban check: this is what makes the ban survive reconnection
            // attempts against a statically configured address too.
            if (_logger.IsDebug) _logger.Debug($"Refusing to reconnect banned beacon chain peer {peerId}");
            return false;
        }

        await _outboundDialGate.WaitAsync(token);
        try
        {
            ISession session = await p2p.DialPeerAsync(Multiaddress.Decode(address), token);
            ManagedPeer peer = new(this, p2p, address, peerId, session);
            if (!await UpdateStatusAsync(peer, token))
            {
                return false;
            }

            _peers[address] = peer;
            Metrics.BeaconChainPeersConnected++;
            Metrics.BeaconChainPeerCount = _peers.Count;
            if (_logger.IsInfo) _logger.Info($"Connected to beacon chain peer {address} (head slot {peer.HeadSlot})");
            return true;
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

    private async Task CheckHealthAsync(ManagedPeer peer, CancellationToken token)
    {
        try
        {
            if (!await UpdateStatusAsync(peer, token))
            {
                return;
            }

            await p2p.PingAsync(peer.Session, token);
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
        StatusMessageV2 status = await p2p.RequestStatusAsync(peer.Session, token);
        peer.RecordMessageSent();
        if (!status.ForkDigest.AsSpan().SequenceEqual(statusSource.CurrentStatus.ForkDigest))
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
        await p2p.GoodbyeAsync(peer.Session, reason, token);
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
        PeerRecord record = GetOrCreateRecord(peerId);
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
        if (consecutiveFaults >= config.FaultDisconnectsBeforeBan && !record.Banned)
        {
            record.Banned = true;
            if (_logger.IsWarn) _logger.Warn($"Banned beacon chain peer {peerId} after {consecutiveFaults} consecutive fault disconnects");
        }
    }

    /// <summary>Internal so a test can assert ban state without dialing: see <see cref="RecordDisconnect(string,long,long,ulong,string)"/>.</summary>
    internal bool IsBannedForTest(string peerId) => IsBanned(peerId);

    /// <summary>The durable, peer-id-keyed half of a peer's history: outlives any one
    /// <see cref="ManagedPeer"/> session so a ban and disconnect history survive reconnection attempts.</summary>
    private sealed class PeerRecord
    {
        public volatile bool Banned;
        public int ConsecutiveFaultDisconnects;
        public int DisconnectCount;
        public long MessagesSent;
        public long FailuresReported;
        public string? LastDisconnectReason;
        public string? LastDisconnectDetail;
    }

    private sealed class ManagedPeer(PeerManager manager, BeaconP2P p2p, string address, string peerId, ISession session) : IBeaconSyncPeer
    {
        private int _consecutiveFailures;
        private long _messagesSent;
        private long _failuresReported;

        public ISession Session { get; } = session;
        public StatusMessageV2? Status { get; set; }

        /// <summary>The libp2p peer id this session was dialed as (see <see cref="PeerManager.ExtractPeerId"/>).</summary>
        public string PeerId { get; } = peerId;

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

        public void ReportFailure(string reason)
        {
            Interlocked.Increment(ref _failuresReported);

            // A closed channel means the session is dead — every further request would fail, so
            // skip the failure budget and let the next maintenance round (or the dial-loop
            // cooldown) reconnect instead of wedging on a zombie session.
            int failures = reason.Contains("Channel closed", StringComparison.OrdinalIgnoreCase) || reason.Contains("session", StringComparison.OrdinalIgnoreCase)
                ? Interlocked.Exchange(ref _consecutiveFailures, MaxConsecutiveFailures)
                : Interlocked.Increment(ref _consecutiveFailures);
            if (manager._logger.IsDebug) manager._logger.Debug($"Beacon chain peer {Id} reported as failing ({failures}/{MaxConsecutiveFailures}): {reason}");
        }
    }
}
