// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
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

    // Peers that never answer each hold a check for a request timeout, so a round checks this many at once.
    private const int MaxConcurrentHealthChecks = 8;

    // Status requests sent outside the health checks at once; each silent peer holds one for two request timeouts (v2, then v1).
    private const int MaxConcurrentStatusRefreshes = 4;

    // A peer that answers status and ping but fails sync requests must stay out of selection for longer than a maintenance round.
    internal static readonly TimeSpan RequestFailureDecayInterval = TimeSpan.FromMinutes(1);

    // A peer that just failed a request is offered after the others for this long; it is never withheld, so a lone custodian still serves.
    internal static readonly TimeSpan RequestFailureCooldown = TimeSpan.FromSeconds(30);

    // With every peer at the request-failure limit, this many of the least-failed are still offered so sync does not wait out the decay.
    private const int MaxPeersOfferedAtFailureLimit = 2;

    // The admission MetaData request holds the dial slot until it returns, so it must not wait out the full request timeout.
    internal static readonly TimeSpan AdmissionMetadataTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);

    // Below MinPeerCount, maintenance (static-peer reconnect and health checks) runs on this
    // shorter cadence instead: the one lever PeerManager itself owns for "more aggressive" behaviour
    // while under-peered. Discovery's own candidate pacing is a different component's concern.
    private static readonly TimeSpan UnderPeeredMaintenanceInterval = TimeSpan.FromSeconds(5);

    // How often WaitForAdmissionCapacityAsync re-checks the target band while parked.
    private static readonly TimeSpan AdmissionPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);
    private const int MaxRedialsAfterSimultaneousDial = 3;
    internal static readonly TimeSpan RedialBackoff = TimeSpan.FromMilliseconds(100);
    private const string PeerIdSeparator = "/p2p/";

    // Bounds the per-peer-id ban/diagnostics table so years of churn on a public network cannot
    // grow it forever. Deterministic policy, see EvictIfOverCapacity: an entry that is not banned goes first.
    private const int MaxTrackedPeerIds = 8192;
    private const int MaxBanMinutes = 10 * 365 * 24 * 60;

    // Not a goodbye code: a closed session exchanged no goodbye, so its disconnect record reads "Other".
    private const ulong SessionClosedReason = 0;
    private const string SessionClosedLabel = "SessionClosed";
    private const string SessionClosedDetail = "its libp2p session closed";

    // Bounds the canonical index walk back over empty slots to a peer's finalized checkpoint block; not a spec value.
    private const ulong FinalizedCheckpointSearchEpochs = 4;

    private readonly BeaconP2P _p2p;
    private readonly IBeaconChainConfig _config;
    private readonly IBeaconChainStatusSource _statusSource;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ManagedPeer> _peers = new();

    // Peers removed for a closed session, by peer id, so an inbound violation reported just after the close still counts against it.
    private readonly ConcurrentDictionary<string, (ManagedPeer Peer, long RemovedAtTicks, long Sequence)> _closedPeers = new(StringComparer.Ordinal);
    // The entries of _closedPeers oldest first, so pruning them costs O(1) per close; written under _admissionLock only.
    private readonly Queue<(string PeerId, long RemovedAtTicks, long Sequence)> _closedOrder = new();
    private long _closedSequence;
    internal static readonly TimeSpan ClosedPeerWindow = TimeSpan.FromMinutes(1);
    private readonly BeaconDiscovery? _discovery;
    private readonly INodeColumnCustodySource _localCustody;
    private readonly ITimestamper _timestamper;
    private readonly PeerDialHistory _dialHistory;
    private readonly BeaconChainStore? _store;
    private readonly BeaconChainSpec? _spec;

    // Stopwatch timestamp of the latest request of ours, to any peer, that was answered.
    private long _lastAnswerAt;

    // Replaced and completed whenever a sampled column is left without a connected custodian or a peer's session closes, to wake the admission wait.
    private TaskCompletionSource _custodyShortfall = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _statusRefreshGate = new(MaxConcurrentStatusRefreshes);

    // The highest slot the node reported the chain has reached past its peers, and when; see RefreshStatusesBehind.
    private ChainAhead? _chainAhead;

    // Ends the status refreshes with Run; none when Run was never started.
    private CancellationToken _runToken;

    /// <summary>The chain reached at least <paramref name="Slot"/> as of <paramref name="SinceTicks"/> (UTC ticks).</summary>
    private sealed record ChainAhead(ulong Slot, long SinceTicks);

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

    // Admissions in flight per session, guarded by _admissionLock: DisconnectUnadmittedAsync leaves a session one of them holds open.
    private readonly Dictionary<ISession, int> _admittingSessions = [];

    /// <summary>What an admission in flight already knows about its peer: enough for the directory's
    /// <see cref="PeerConnectionState.Connecting"/> entry and for the peer-id dedup in <see cref="IsKnown"/>.
    /// <paramref name="Enr"/> is only ever known for a discovery-sourced dial (see
    /// <see cref="TryAddPeerAsync"/>'s optional parameter); <c>null</c> for a static-peer reconnect or
    /// an inbound session, which have no ENR to offer.</summary>
    private readonly record struct Reservation(string PeerId, PeerDirection Direction, string? Enr);

    /// <param name="discovery">Supplies this node's sampled columns and dials their custodians; without it no custody is sought or kept.</param>
    /// <param name="timestamper">The clock the request-failure decay reads; the system clock when omitted.</param>
    /// <param name="store">The canonical index a peer's older finalized checkpoint is checked against; without it only our own finalized epoch is checked.</param>
    /// <param name="spec">The epoch length for that check.</param>
    public PeerManager(BeaconP2P p2p, IBeaconChainConfig config, IBeaconChainStatusSource statusSource, ILogManager logManager, BeaconDiscovery? discovery = null, ITimestamper? timestamper = null,
        BeaconChainStore? store = null, BeaconChainSpec? spec = null)
    {
        _store = store;
        _spec = spec;
        _timestamper = timestamper ?? Timestamper.Default;
        _p2p = p2p;
        _config = config;
        _statusSource = statusSource;
        _logger = logManager.GetClassLogger<PeerManager>();
        _discovery = discovery;
        _dialHistory = discovery?.DialHistory ?? new PeerDialHistory(_timestamper);
        _localCustody = new DiscoveryNodeCustodySource(discovery);
        _outboundDialGate = new SemaphoreSlim(Math.Max(1, config.MaxConcurrentOutboundDials));
        ValidateStaticPeers(StaticPeerAddresses());

        // A session the remote side opened has no dial here to admit it through; this is its only way in.
        p2p.SessionEstablished += OnSessionEstablished;
    }

    /// <inheritdoc/>
    public event Action<IBeaconSyncPeer>? PeerAdmitted;

    /// <summary>The number of connected, status-exchanged peers.</summary>
    public int PeerCount => _peers.Count;

    public async Task Run(CancellationToken token)
    {
        _runToken = token;
        _ = Task.Run(() => RefreshStatusesOnCadenceAsync(token));
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

    /// <summary>How old a peer's <c>status</c> may get before it is asked again, whatever the health checks do; settable so a test need not wait it out.</summary>
    internal TimeSpan StatusRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The least time between two <c>status</c> requests to one peer outside the health checks; settable so a test need not wait it out.</summary>
    internal TimeSpan MinStatusRefreshInterval { get; set; } = TimeSpan.FromSeconds(12);

    /// <inheritdoc/>
    /// <remarks>
    /// phase0/p2p-interface.md Status: a client may send Status again to learn whether a peer has a higher head. Until a peer whose
    /// last status is older than <paramref name="slot"/> answers, <see cref="GetBestPeers"/> may still offer it for slots up to <paramref name="slot"/>.
    /// </remarks>
    public void RefreshStatusesBehind(ulong slot, string reason)
    {
        long now = _timestamper.UtcNowOffset.UtcTicks;
        ChainAhead? current = Volatile.Read(ref _chainAhead);
        while ((current is null || current.Slot < slot) && Interlocked.CompareExchange(ref _chainAhead, new ChainAhead(slot, now), current) != current)
        {
            current = Volatile.Read(ref _chainAhead);
        }

        RefreshStatusesBelow(slot, reason);
    }

    /// <inheritdoc/>
    public void RefreshStatusesBelow(ulong slot, string reason) => RefreshStatuses(peer => peer.HeadSlot < slot, reason);

    /// <summary>Asks every connected peer that is <paramref name="due"/>, has no <c>status</c> request outstanding and was not asked within <see cref="MinStatusRefreshInterval"/>; logs once when any is asked.</summary>
    /// <remarks>Runs off the caller's thread, at most <see cref="MaxConcurrentStatusRefreshes"/> requests at once; a failure only marks the peer's status stale.</remarks>
    private void RefreshStatuses(Func<ManagedPeer, bool> due, string reason)
    {
        long now = _timestamper.UtcNowOffset.UtcTicks;
        List<ManagedPeer> asked = [];
        foreach (KeyValuePair<string, ManagedPeer> peer in _peers)
        {
            if (due(peer.Value) && peer.Value.TryClaimStatusRefresh(now, MinStatusRefreshInterval.Ticks))
            {
                asked.Add(peer.Value);
            }
        }

        if (asked.Count == 0)
        {
            return;
        }

        if (_logger.IsDebug) _logger.Debug($"Refreshing the status of {asked.Count} beacon chain peer{(asked.Count == 1 ? "" : "s")}: {reason}");
        CancellationToken token = _runToken;
        foreach (ManagedPeer peer in asked)
        {
            _ = Task.Run(() => RefreshStatusAsync(peer, token));
        }
    }

    private async Task RefreshStatusAsync(ManagedPeer peer, CancellationToken token)
    {
        try
        {
            await _statusRefreshGate.WaitAsync(token);
            try
            {
                await UpdateStatusAsync(peer, token);
            }
            finally
            {
                _statusRefreshGate.Release();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Status refresh of beacon chain peer {peer.Id} failed: {DescribeFailure(e)}");
        }
        finally
        {
            peer.ReleaseStatusRefresh();
        }
    }

    /// <summary>Refreshes, every <see cref="StatusRefreshInterval"/>, the <c>status</c> of each peer not heard from within it, so a health check that keeps failing does not leave it stale.</summary>
    private async Task RefreshStatusesOnCadenceAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TimeSpan interval = StatusRefreshInterval;
            await Task.Delay(interval, token);
            long receivedBefore = _timestamper.UtcNowOffset.UtcTicks - interval.Ticks;
            RefreshStatuses(peer => peer.StatusReceivedTicks < receivedBefore, $"no status for {interval.TotalSeconds:F0} s");
        }
    }

    /// <summary>
    /// Backpressure for the discovery dial loop: the loop should ask whether there is room rather
    /// than deciding for itself from raw config, so this is the one place "at target" is defined.
    /// Returns once the pool is below <see cref="IBeaconChainConfig.TargetPeerCount"/> counting both
    /// connected peers and dials already admitted but not yet resolved, so a burst of concurrent
    /// dials cannot itself blow through the target the moment they all land.
    /// </summary>
    /// <remarks>
    /// Also returns while a sampled column has no connected custodian; <see cref="TryAddPeerAsync"/> then admits past the target
    /// only a candidate custodying such a column, making room for it at <see cref="IBeaconChainConfig.MaxPeerCount"/>.
    /// </remarks>
    public async Task WaitForAdmissionCapacityAsync(CancellationToken token)
    {
        while (true)
        {
            Task shortfall = Volatile.Read(ref _custodyShortfall).Task;
            if (_peers.Count + _dialing.Count < _config.TargetPeerCount || UncustodiedSampledColumns().Count > 0)
            {
                return;
            }

            using CancellationTokenSource poll = CancellationTokenSource.CreateLinkedTokenSource(token);
            await Task.WhenAny(Task.Delay(AdmissionPollInterval, poll.Token), shortfall);
            await poll.CancelAsync();
            token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The columns this node samples (fulu/das-core.md) that no connected peer below the failure limit custodies; empty while this node's custody is unknown.</summary>
    internal IReadOnlyList<ulong> UncustodiedSampledColumns()
    {
        if (_localCustody.Current is not { } local)
        {
            return [];
        }

        List<ulong> uncustodied = [];
        foreach (ulong column in local.SampledColumns)
        {
            if (CustodianCount(column, usableOnly: true) == 0)
            {
                uncustodied.Add(column);
            }
        }

        return uncustodied;
    }

    /// <param name="usableOnly">Counts only peers below the request-failure limit, which requests go to while any such peer is connected (see <see cref="GetBestPeers"/>).</param>
    private int CustodianCount(ulong column, bool usableOnly = false)
    {
        int count = 0;
        foreach (KeyValuePair<string, ManagedPeer> peer in _peers)
        {
            if (peer.Value.Custody.Custodies(column) && !(usableOnly && peer.Value.IsAtFailureLimit))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Hands discovery the sampled columns left without a connected custodian, and wakes the admission wait when there are any.</summary>
    private void PublishCustodyShortfall()
    {
        IReadOnlyList<ulong> uncustodied = UncustodiedSampledColumns();
        _discovery?.RequestColumnCustodians(uncustodied);
        if (uncustodied.Count > 0)
        {
            WakeAdmissionWaiters();
        }
    }

    private void WakeAdmissionWaiters() =>
        Interlocked.Exchange(ref _custodyShortfall, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

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
                await ConnectAsync(address, token, token, backoff: false);
            }
        }

        // Enumerating the concurrent dictionary tolerates a check dropping a peer meanwhile.
        await Parallel.ForEachAsync(_peers, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentHealthChecks, CancellationToken = token },
            (peer, checkToken) => new ValueTask(CheckHealthAsync(peer.Value, checkToken)));

        await TrimToPeerBandAsync(staticAddresses, token);
        PublishCustodyShortfall();
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
    /// The last connected custodian of a column this node samples is kept too, since without it the node
    /// cannot retrieve that column (fulu/das-core.md); the pool can then stay above the target.
    /// </remarks>
    private async Task TrimToPeerBandAsync(string[] staticAddresses, CancellationToken token)
    {
        int overflow = _peers.Count - _config.MaxPeerCount;
        if (overflow <= 0)
        {
            return;
        }

        List<ManagedPeer> trimmable = TrimmableWorstFirst(staticAddresses);
        IReadOnlyList<ulong> sampled = _localCustody.Current?.SampledColumns ?? [];
        int[] custodians = CustodianCounts(sampled);
        int target = Math.Min(_config.TargetPeerCount, _config.MaxPeerCount);
        int toDrop = Math.Min(trimmable.Count, _peers.Count - target);
        for (int i = 0; i < trimmable.Count && toDrop > 0; i++)
        {
            PeerColumnCustody custody = trimmable[i].Custody;
            if (IsLastCustodian(custody, sampled, custodians))
            {
                continue;
            }

            for (int c = 0; c < sampled.Count; c++)
            {
                if (custody.Custodies(sampled[c]))
                {
                    custodians[c]--;
                }
            }

            toDrop--;
            await DropAsync(trimmable[i], GoodbyeReason.TooManyPeers, "over the configured peer band", token);
        }
    }

    /// <summary>The connected peers other than configured static ones, worst first: most consecutive health-check failures, then stalest head.</summary>
    private List<ManagedPeer> TrimmableWorstFirst(string[] staticAddresses)
    {
        // By peer id, not pool key: a static peer that connected to us first is keyed by the address it
        // came from, which never equals its configured dial address.
        HashSet<string> exempt = new(StringComparer.Ordinal);
        foreach (string address in staticAddresses)
        {
            exempt.Add(ExtractPeerId(address));
        }

        List<ManagedPeer> trimmable = [];
        foreach (KeyValuePair<string, ManagedPeer> peer in _peers)
        {
            if (!exempt.Contains(peer.Value.PeerId))
            {
                trimmable.Add(peer.Value);
            }
        }

        trimmable.Sort(static (a, b) =>
        {
            int byFailures = b.ConsecutiveFailures.CompareTo(a.ConsecutiveFailures);
            return byFailures != 0 ? byFailures : a.HeadSlot.CompareTo(b.HeadSlot);
        });
        return trimmable;
    }

    private int[] CustodianCounts(IReadOnlyList<ulong> sampled)
    {
        int[] custodians = new int[sampled.Count];
        for (int c = 0; c < sampled.Count; c++)
        {
            custodians[c] = CustodianCount(sampled[c]);
        }

        return custodians;
    }

    private static bool IsLastCustodian(PeerColumnCustody custody, IReadOnlyList<ulong> sampled, int[] custodians)
    {
        for (int c = 0; c < sampled.Count; c++)
        {
            if (custodians[c] == 1 && custody.Custodies(sampled[c]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The worst non-static peer that would not be the last custodian of a sampled column once <paramref name="joining"/> is connected too.</summary>
    private ManagedPeer? WorstReplaceablePeer(PeerColumnCustody joining)
    {
        IReadOnlyList<ulong> sampled = _localCustody.Current?.SampledColumns ?? [];
        int[] custodians = CustodianCounts(sampled);
        for (int c = 0; c < sampled.Count; c++)
        {
            if (joining.Custodies(sampled[c]))
            {
                custodians[c]++;
            }
        }

        foreach (ManagedPeer peer in TrimmableWorstFirst(StaticPeerAddresses()))
        {
            if (!IsLastCustodian(peer.Custody, sampled, custodians))
            {
                return peer;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A peer at the request-failure limit is not handed out while another peer is, but it is not dropped for request failures alone either: it can be the
    /// last custodian of a sampled column (fulu/das-core.md), so its columns are sought elsewhere while it stays connected.
    /// A passing health check does not readmit it: it returns once it serves a request or its failures decay (<see cref="RequestFailureDecayInterval"/> each).
    /// A peer that failed a request within <see cref="RequestFailureCooldown"/> is listed after every peer that did not, whatever its head slot, so a batch that
    /// chose it once does not choose it again while others exist; it is still listed, and serves when it is the only peer that can.
    /// When no peer qualifies and <see cref="RefreshStatusesBehind"/> reported the chain at or past <paramref name="minHeadSlot"/>, the peers whose status
    /// predates that report and could not be refreshed since are listed instead, whatever their head slot.
    /// When neither leaves a peer, at most <see cref="MaxPeersOfferedAtFailureLimit"/> of the least-failed peers at the limit are listed, by the same head rules,
    /// so sync does not wait for a failure to decay; a peer with invalid data in its penalty, held out of selection or with a closed session is never listed.
    /// </remarks>
    public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot)
    {
        List<ManagedPeer> best = [];
        List<ManagedPeer> stale = [];
        List<ManagedPeer> atLimit = [];
        List<ManagedPeer> staleAtLimit = [];
        int noStatus = 0, behind = 0, failing = 0;
        ChainAhead? ahead = Volatile.Read(ref _chainAhead);
        foreach (KeyValuePair<string, ManagedPeer> peer in _peers)
        {
            if (peer.Value.Status is null)
            {
                noStatus++;
            }
            else if (peer.Value.HeadSlot < minHeadSlot)
            {
                bool staleStatus = ahead is not null && ahead.Slot >= minHeadSlot && peer.Value.IsStatusStaleSince(ahead.SinceTicks);
                if (staleStatus && !peer.Value.IsAtFailureLimit)
                {
                    stale.Add(peer.Value);
                }
                else
                {
                    behind++;
                    if (staleStatus && peer.Value.IsOfferableAtFailureLimit)
                    {
                        staleAtLimit.Add(peer.Value);
                    }
                }
            }
            else if (peer.Value.IsAtFailureLimit)
            {
                failing++;
                if (peer.Value.IsOfferableAtFailureLimit)
                {
                    atLimit.Add(peer.Value);
                }
            }
            else
            {
                best.Add(peer.Value);
            }
        }

        long now = _timestamper.UtcNowOffset.UtcTicks;
        bool offerStale = best.Count == 0 && stale.Count > 0;
        if (!offerStale)
        {
            behind += stale.Count;
        }

        List<ManagedPeer> leastFailed = best.Count == 0 && stale.Count == 0 ? LeastFailed(atLimit.Count > 0 ? atLimit : staleAtLimit, now) : [];
        if (noStatus + behind + failing + (offerStale ? stale.Count : 0) > 0 && _logger.IsDebug) _logger.Debug($"Sync peers for {(minHeadSlot == 0 ? "any head" : $"head slot {minHeadSlot}")}: {best.Count} usable; left out {noStatus} without status, {behind} behind, {failing} at the request-failure limit{(offerStale ? $"; offering {stale.Count} whose status predates slot {ahead!.Slot}" : "")}{(leastFailed.Count > 0 ? $"; offering the {leastFailed.Count} least-failed at the request-failure limit" : "")}");
        if (offerStale)
        {
            return OrderForSelection(stale, peer => peer.IsCoolingDown(now), static peer => peer.RequestsInFlight, static peer => peer.HeadSlot);
        }

        return OrderForSelection(best.Count > 0 ? best : leastFailed, peer => peer.IsCoolingDown(now), static peer => peer.RequestsInFlight, static peer => peer.HeadSlot);
    }

    /// <summary>The <see cref="MaxPeersOfferedAtFailureLimit"/> peers of <paramref name="peers"/> with the fewest request failures, then the fewest consecutive failures,
    /// then first in <see cref="OrderForSelection{T}"/>.</summary>
    /// <remarks>Request failures are held at the limit, so the consecutive count, which a passing health check clears, separates peers at the limit.</remarks>
    private static List<ManagedPeer> LeastFailed(List<ManagedPeer> peers, long now)
    {
        if (peers.Count == 0)
        {
            return peers;
        }

        ManagedPeer[] ordered = OrderForSelection(peers, peer => peer.IsCoolingDown(now), static peer => peer.RequestsInFlight, static peer => peer.HeadSlot);
        // Read once per peer: a failure that lands mid-sort would make a live comparator inconsistent.
        (int RequestFailures, int ConsecutiveFailures, int Rank, ManagedPeer Peer)[] keyed = new (int, int, int, ManagedPeer)[ordered.Length];
        for (int i = 0; i < keyed.Length; i++)
        {
            keyed[i] = (ordered[i].RequestFailures, ordered[i].ConsecutiveFailures, i, ordered[i]);
        }

        Array.Sort(keyed, static (a, b) =>
        {
            int byRequestFailures = a.RequestFailures.CompareTo(b.RequestFailures);
            if (byRequestFailures != 0)
            {
                return byRequestFailures;
            }

            int byConsecutiveFailures = a.ConsecutiveFailures.CompareTo(b.ConsecutiveFailures);
            return byConsecutiveFailures != 0 ? byConsecutiveFailures : a.Rank.CompareTo(b.Rank);
        });

        int count = Math.Min(MaxPeersOfferedAtFailureLimit, keyed.Length);
        List<ManagedPeer> least = new(count);
        for (int i = 0; i < count; i++)
        {
            least.Add(keyed[i].Peer);
        }

        return least;
    }

    /// <summary>Orders peers that did not fail a request recently before those that did, then the least busy first, then by head slot, best first.</summary>
    /// <remarks>
    /// Each key is read once per peer before sorting: a failure or status that lands mid-sort would make a live comparator inconsistent, and the sort throws on that.
    /// Requests started one after another then go to different peers, where each peer takes at most <see cref="ReqRespProtocolBase.MaxConcurrentRequests"/> of a protocol at once.
    /// </remarks>
    internal static T[] OrderForSelection<T>(IReadOnlyList<T> peers, Func<T, bool> isCoolingDown, Func<T, int> requestsInFlight, Func<T, ulong> headSlot)
    {
        (bool Cooling, int InFlight, ulong HeadSlot, T Peer)[] keyed = new (bool, int, ulong, T)[peers.Count];
        for (int i = 0; i < keyed.Length; i++)
        {
            keyed[i] = (isCoolingDown(peers[i]), requestsInFlight(peers[i]), headSlot(peers[i]), peers[i]);
        }

        Array.Sort(keyed, static (a, b) =>
        {
            int byCooldown = a.Cooling.CompareTo(b.Cooling);
            if (byCooldown != 0)
            {
                return byCooldown;
            }

            int byLoad = a.InFlight.CompareTo(b.InFlight);
            return byLoad != 0 ? byLoad : b.HeadSlot.CompareTo(a.HeadSlot);
        });

        T[] ordered = new T[keyed.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            ordered[i] = keyed[i].Peer;
        }

        return ordered;
    }

    /// <summary>
    /// Records a protocol violation by the requester of an inbound stream against the connected peer with this id, also when selection leaves
    /// that peer out (at the request-failure limit, or behind a head slot): it is still the one that broke the protocol.
    /// </summary>
    /// <returns><c>false</c> when no connected peer has this id and none was removed for a closed session within <see cref="ClosedPeerWindow"/>.</returns>
    /// <remarks>
    /// Not a failed request of ours, so the peer is not put behind others in <see cref="GetBestPeers"/>.
    /// A peer removed for its closed session still takes the violation, which turns that close into a fault disconnect (see <see cref="RemoveClosedSession"/>).
    /// </remarks>
    internal bool TryReportInboundViolation(PeerId peerId, string detail)
    {
        ManagedPeer? connected;
        // Under the lock the close removal holds, so the peer is found in the pool or among the closed ones, never between.
        lock (_admissionLock)
        {
            if (!TryFindConnected(peerId.ToString(), out connected) && !TryFindClosed(peerId.ToString(), out connected))
            {
                return false;
            }
        }

        connected!.ReportFailure(PeerFailureReason.ProtocolViolation, detail, ownRequest: false);
        return true;
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
    /// <remarks>
    /// At or above <see cref="IBeaconChainConfig.TargetPeerCount"/>, once this node's custody is known, a candidate is dialed only
    /// when <paramref name="enr"/> custodies a sampled column no connected peer custodies (see <see cref="WaitForAdmissionCapacityAsync"/>);
    /// at <see cref="IBeaconChainConfig.MaxPeerCount"/> such a candidate is dialed one over the ceiling and, once admitted, takes the place of the worst
    /// peer that is not the last custodian of a sampled column; with no such peer it is not dialed.
    /// </remarks>
    public async Task<bool> TryAddPeerAsync(string address, CancellationToken token, string? enr = null)
    {
        if (!address.Contains(PeerIdSeparator, StringComparison.Ordinal))
        {
            if (_logger.IsDebug) _logger.Debug($"Not dialing {address}: the address has no {PeerIdSeparator} peer id");
            return false;
        }

        if (IsConnected(address))
        {
            return true;
        }

        if (!IsWantedAtCurrentCount(enr, out PeerColumnCustody? covering))
        {
            if (_logger.IsDebug) _logger.Debug($"Not dialing {address}: at the target peer count and it custodies no sampled column that lacks a connected custodian");
            return false;
        }

        bool replacing = covering is not null && _peers.Count + _dialing.Count >= _config.MaxPeerCount;
        if (replacing && WorstReplaceablePeer(covering!) is null)
        {
            if (_logger.IsDebug) _logger.Debug($"Not dialing {address}: at the peer band ceiling and every peer is the last custodian of a sampled column");
            return false;
        }

        bool admitted;
        try
        {
            admitted = await ConnectAsync(address, token, token, enr, overCeiling: replacing);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug($"Dialing beacon chain peer {address} timed out");
            return false;
        }

        // Only after the dial, so a candidate that cannot be reached costs no connected peer.
        if (admitted && replacing && _peers.Count > _config.MaxPeerCount && WorstReplaceablePeer(PeerColumnCustody.None) is { } replaced)
        {
            await DropAsync(replaced, GoodbyeReason.TooManyPeers, "at the peer band ceiling, replaced by a custodian of a sampled column no other connected peer custodies", token);
        }

        return admitted;
    }

    /// <summary>Dials discovered candidates, at most <see cref="IBeaconChainConfig.MaxConcurrentOutboundDials"/> at once, while the pool has room; returns once every dial has ended.</summary>
    internal Task DialDiscoveredPeersAsync(IAsyncEnumerable<BeaconPeerCandidate> candidates, CancellationToken token)
        => ScheduleDialsAsync(candidates, Math.Max(1, _config.MaxConcurrentOutboundDials), WaitForAdmissionCapacityAsync, DialCandidateAsync,
            (candidate, e) => { if (_logger.IsDebug) _logger.Debug($"Dialing discovered beacon chain peer {candidate.Multiaddress} failed: {DescribeFailure(e)}"); }, token);

    /// <param name="failed">Receives a dial's failure, which is contained to its candidate so one bad record cannot end the schedule.</param>
    internal static Task ScheduleDialsAsync(IAsyncEnumerable<BeaconPeerCandidate> candidates, int concurrency,
        Func<CancellationToken, Task> waitForCapacity, Func<BeaconPeerCandidate, CancellationToken, Task> dial, Action<BeaconPeerCandidate, Exception> failed, CancellationToken token)
        => Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token },
            async (candidate, dialToken) =>
            {
                await waitForCapacity(dialToken);
                try
                {
                    await dial(candidate, dialToken);
                }
                catch (Exception e) when (e is not OperationCanceledException || !dialToken.IsCancellationRequested)
                {
                    failed(candidate, e);
                }
            });

    /// <summary>Dials the candidate's selected address, then its other advertised addresses until one is admitted.</summary>
    private async Task DialCandidateAsync(BeaconPeerCandidate candidate, CancellationToken token)
    {
        if (await TryAddPeerAsync(candidate.Multiaddress, token, candidate.Enr)) return;
        foreach (string address in candidate.Addresses)
        {
            if (!string.Equals(address, candidate.Multiaddress, StringComparison.Ordinal) &&
                await TryAddPeerAsync(address, token, candidate.Enr)) return;
        }
    }

    /// <param name="covering">The custody of <paramref name="enr"/> when the admission rests on it custodying a sampled column no connected peer custodies.</param>
    private bool IsWantedAtCurrentCount(string? enr, out PeerColumnCustody? covering)
    {
        covering = null;
        if (_peers.Count + _dialing.Count < _config.TargetPeerCount || _localCustody.Current is null)
        {
            return true;
        }

        IReadOnlyList<ulong> uncustodied = UncustodiedSampledColumns();
        if (uncustodied.Count > 0 && PeerColumnCustody.ForEnr(enr) is { } custody && custody.CountCustodied(uncustodied) > 0)
        {
            covering = custody;
        }

        return covering is not null;
    }

    /// <summary>
    /// Atomically checks the ceiling and reserves <paramref name="address"/> as one step, so
    /// concurrent admissions cannot all observe the same stale count and all pass (the exact
    /// overshoot this closes: up to MaxConcurrentOutboundDials could previously admit past
    /// MaxPeerCount). Also refuses a peer id already connected or in flight under any address, so
    /// one session can never be recorded twice; <paramref name="atCeiling"/> tells the two refusals
    /// apart, because only the ceiling one may cost the remote its session.
    /// </summary>
    /// <param name="overCeiling">Allows one admission past the ceiling in total, for a dial that replaces a connected peer once admitted.</param>
    private bool TryReserveAdmissionSlot(string address, string peerId, PeerDirection direction, string? enr, out bool atCeiling, bool overCeiling = false)
    {
        lock (_admissionLock)
        {
            if (_dialing.ContainsKey(address) || IsKnown(peerId))
            {
                atCeiling = false;
                return false;
            }

            atCeiling = _peers.Count + _dialing.Count >= _config.MaxPeerCount + (overCeiling ? 1 : 0);
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

    private bool HoldsSession(ISession session)
    {
        foreach (KeyValuePair<string, ManagedPeer> connected in _peers)
        {
            if (ReferenceEquals(connected.Value.Session, session))
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
                IsBanActive(record.Value, _timestamper.UtcNowOffset)));
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

    /// <summary>Refuses a static peer the maintenance round could never dial.</summary>
    /// <remarks>The libp2p dial needs the <c>/p2p/</c> peer id, and it keeps a dial whose name resolution failed as the
    /// answer for that peer id from then on, so a static peer must name its IP address.</remarks>
    /// <exception cref="InvalidConfigurationException">An address does not decode, has no peer id, or names a DNS host.</exception>
    private static void ValidateStaticPeers(string[] addresses)
    {
        foreach (string address in addresses)
        {
            Multiaddress multiaddress;
            try
            {
                multiaddress = Multiaddress.Decode(address);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new InvalidConfigurationException($"BeaconChain.StaticPeers entry '{address}' is not a multiaddr: {e.Message}", ExitCodes.ForbiddenOptionValue);
            }

            if (multiaddress.GetPeerId() is null || !(multiaddress.Has<IP4>() || multiaddress.Has<IP6>()))
            {
                throw new InvalidConfigurationException($"BeaconChain.StaticPeers entry '{address}' must be /ip4 or /ip6 with a /p2p/<peer-id> component.", ExitCodes.ForbiddenOptionValue);
            }
        }
    }

    /// <summary>The stable part of a dial address to key the ban list and diagnostics on: the libp2p peer id after the last <c>/p2p/</c> component.</summary>
    /// <exception cref="ArgumentException"><paramref name="address"/> has no <c>/p2p/</c> component.</exception>
    private static string ExtractPeerId(string address)
    {
        int index = address.LastIndexOf(PeerIdSeparator, StringComparison.Ordinal);
        return index < 0
            ? throw new ArgumentException($"{address} has no {PeerIdSeparator} peer id", nameof(address))
            : address[(index + PeerIdSeparator.Length)..];
    }

    /// <summary>The peer id a dial address names, or <c>null</c> when it does not decode.</summary>
    private static PeerId? TryDecodePeerId(string address)
    {
        try
        {
            return Multiaddress.Decode(address).GetPeerId();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Internal so a test can check the ban key derivation directly.</summary>
    internal static string ExtractPeerIdForTest(string address) => ExtractPeerId(address);

    private bool IsBanned(string peerId) => _peerRecords.TryGetValue(peerId, out BanRecord? record) && IsBanActive(record, _timestamper.UtcNowOffset);

    private TimeSpan BanDuration => TimeSpan.FromMinutes(Math.Clamp(_config.PeerBanMinutes, 0, MaxBanMinutes));

    /// <summary>Whether the ban on <paramref name="record"/> still holds at <paramref name="now"/>. A ban that has run out is lifted
    /// here together with the fault streak behind it, so a peer that was only slow starts again from a clean history.</summary>
    private bool IsBanActive(BanRecord record, DateTimeOffset now)
    {
        long bannedUntil = Volatile.Read(ref record.BannedUntilTicks);
        if (bannedUntil == 0)
        {
            return false;
        }

        if (now.UtcTicks < bannedUntil)
        {
            return true;
        }

        lock (record)
        {
            if (record.BannedUntilTicks == bannedUntil)
            {
                record.BannedUntilTicks = 0;
                record.ConsecutiveFaultDisconnects = 0;
            }
        }

        return false;
    }

    private BanRecord GetOrCreateRecord(string peerId)
    {
        if (_peerRecords.TryGetValue(peerId, out BanRecord? existing))
        {
            return existing;
        }

        EvictIfOverCapacity();
        return _peerRecords.GetOrAdd(peerId, _ => new BanRecord(Interlocked.Increment(ref _nextRecordSequence)));
    }

    /// <summary>Deterministic bound on the ban/diagnostics table: evicts the oldest entry that is not banned, by creation order.
    /// When every entry is banned, the ban closest to running out goes, so the table never exceeds the cap.</summary>
    private void EvictIfOverCapacity()
    {
        if (_peerRecords.Count < MaxTrackedPeerIds)
        {
            return;
        }

        DateTimeOffset now = _timestamper.UtcNowOffset;
        string? oldestKey = null;
        long oldestSequence = long.MaxValue;
        string? soonestBanKey = null;
        long soonestBan = long.MaxValue;
        foreach (KeyValuePair<string, BanRecord> entry in _peerRecords)
        {
            if (IsBanActive(entry.Value, now))
            {
                long bannedUntil = Volatile.Read(ref entry.Value.BannedUntilTicks);
                if (bannedUntil != 0 && bannedUntil < soonestBan)
                {
                    soonestBan = bannedUntil;
                    soonestBanKey = entry.Key;
                }
            }
            else if (entry.Value.Sequence < oldestSequence)
            {
                oldestSequence = entry.Value.Sequence;
                oldestKey = entry.Key;
            }
        }

        if ((oldestKey ?? soonestBanKey) is { } evicted)
        {
            _peerRecords.TryRemove(evicted, out _);
        }
    }

    /// <summary>
    /// The one outbound admission path: the static-peer reconnect loop and discovery dials both come
    /// through here, so the ban check and the ceiling reservation live here and not in one caller
    /// (the static path used to skip the reservation and could take the pool past MaxPeerCount).
    /// Refuses a banned id, a peer already connected or in flight, or one that would overshoot the
    /// ceiling, all without attempting a dial.
    /// </summary>
    /// <param name="callerToken">The caller's own token; once it is cancelled nothing is admitted after the dial ends.</param>
    /// <param name="token">Cancels the dial: <paramref name="callerToken"/> or a timeout linked to it.</param>
    /// <param name="overCeiling">See <see cref="TryReserveAdmissionSlot"/>.</param>
    /// <param name="backoff">Refuses an address whose earlier dials failed until its backoff ends; a configured static peer is dialed regardless.</param>
    private async Task<bool> ConnectAsync(string address, CancellationToken callerToken, CancellationToken token, string? enr = null, bool overCeiling = false, bool backoff = true)
    {
        string peerId = ExtractPeerId(address);
        if (IsBanned(peerId) || (backoff && !_dialHistory.CanDial(address)))
        {
            if (_logger.IsDebug) _logger.Debug($"Refusing to dial banned or cooling down beacon chain peer {peerId}");
            return false;
        }

        if (!TryReserveAdmissionSlot(address, peerId, PeerDirection.Outbound, enr, out bool atCeiling, overCeiling))
        {
            if (_logger.IsDebug) _logger.Debug($"Refusing to dial {address}: {(atCeiling ? $"at the configured peer band ceiling ({_config.MaxPeerCount})" : "already connected or in flight")}");
            return false;
        }

        DialRecord dial = new(_dialHistory, address);
        try
        {
            using CancellationTokenSource dialCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using BeaconP2P.SessionWatch? sessions = TryDecodePeerId(address) is { } remotePeerId ? _p2p.WatchSessions(remotePeerId) : null;
            try
            {
                Interlocked.Increment(ref Metrics.DialAttemptsCount);
                DialOutcome outcome = await DialAndAdmitAsync(address, peerId, enr, dial, dialCancellation.Token, () => dialCancellation.CancelAfter(DialTimeout));
                // The peer can still hold its half of the collapsed session when a redial arrives and refuses it, so redials back off.
                for (int redials = 1; redials <= MaxRedialsAfterSimultaneousDial
                    && outcome.LostSessionPeerId is { } lostPeerId && sessions?.Opened > 0 && RedialsAfterSimultaneousDial(lostPeerId); redials++)
                {
                    await Task.Delay(RedialBackoff * redials, dialCancellation.Token);
                    outcome = await DialAndAdmitAsync(address, peerId, enr, dial, dialCancellation.Token);
                }

                token.ThrowIfCancellationRequested();
                dial.End(outcome.Admitted);
                return outcome.Admitted;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                dial.End(admitted: false);
                return false;
            }
        }
        finally
        {
            dial.End(admitted: null);
            _dialing.TryRemove(address, out _);
            _ = AdmitSessionLeftUnclaimedAsync(address, callerToken);
        }
    }

    /// <summary>Admits the session the dialed peer opened to us while the dial held its reservation.</summary>
    /// <remarks>The session-established event skips a peer id with a dial in flight (see <see cref="OnSessionEstablished"/>),
    /// so a dial that ends without admitting would otherwise leave that session open outside the band and the health checks.</remarks>
    private async Task AdmitSessionLeftUnclaimedAsync(string address, CancellationToken callerToken)
    {
        try
        {
            if (Multiaddress.Decode(address).GetPeerId() is not { } remotePeerId
                || !_p2p.TryGetEstablishedSession(remotePeerId, out ISession? session))
            {
                return;
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            cts.CancelAfter(DialTimeout);
            BeaconP2P.SessionInfo info = await _p2p.GetSessionInfoAsync(session, cts.Token);
            if (!callerToken.IsCancellationRequested)
            {
                OnSessionEstablished(session, info);
            }
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // The caller is shutting down: there is nothing to admit and nothing worth logging.
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Admitting the session beacon chain peer {address} opened during our dial failed: {DescribeFailure(e)}");
        }
    }

    /// <summary>The dial history entry of one outbound dial, written once: a fault close of the peer it admitted makes it a failure whenever that lands.</summary>
    private sealed class DialRecord(PeerDialHistory history, string address)
    {
        private readonly Lock _lock = new();
        private bool _ended;
        private bool _failed;
        private bool _failureRecorded;

        /// <param name="admitted">The dial's outcome; <c>null</c> when the caller cancelled it, which records nothing unless the peer failed.</param>
        public void End(bool? admitted)
        {
            lock (_lock)
            {
                if (_ended)
                {
                    return;
                }

                _ended = true;
                if (_failed || admitted == false)
                {
                    history.Record(address, connected: false);
                    _failureRecorded = true;
                }
                else if (admitted == true)
                {
                    history.Record(address, connected: true);
                }
            }
        }

        public void Fail()
        {
            lock (_lock)
            {
                _failed = true;
                if (_ended && !_failureRecorded)
                {
                    history.Record(address, connected: false);
                    _failureRecorded = true;
                }
            }
        }
    }

    /// <param name="LostSessionPeerId">The peer id of a dial whose connection opened but whose session the libp2p layer refused or
    /// dropped before identify completed.</param>
    private readonly record struct DialOutcome(bool Admitted, string? LostSessionPeerId = null);

    /// <summary>Whether a dial whose session closed before identify completed is tried again.</summary>
    /// <remarks>Two peers that dial each other at once can each keep the session it opened and refuse the other's
    /// as a second session, which closes both connections. Only the side with the lower peer id redials, so redials never
    /// cross each other, and the other side admits one as a session the remote opened. The peer can still hold its half of the
    /// collapsed session for a while and refuse a redial as a second session, which is why the caller backs off and retries.
    /// The caller redials only once a session with the peer opened during the dial: a connection that never authenticated the
    /// peer, as to an unreachable or dead address, is no sign of a crossing dial.</remarks>
    private bool RedialsAfterSimultaneousDial(string remotePeerId) =>
        _p2p.LocalPeerId is { } localPeerId && string.CompareOrdinal(localPeerId.ToString(), remotePeerId) < 0;

    private async Task<DialOutcome> DialAndAdmitAsync(string address, string peerId, string? enr, DialRecord dial, CancellationToken token, Action? started = null)
    {
        await _outboundDialGate.WaitAsync(token);
        ISession? session = null;
        bool admissionResolved = false;
        try
        {
            started?.Invoke();
            session = await _p2p.DialPeerAsync(Multiaddress.Decode(address), token);
            // The dial returns before the agent probe has answered, and may hand back a session that
            // already existed (the peer connected to us first): wait for what the libp2p layer
            // recorded instead of assuming "we dialed it, no client string".
            BeaconP2P.SessionInfo info;
            try
            {
                info = await _p2p.GetSessionInfoAsync(session, token);
            }
            catch (InvalidOperationException e)
            {
                if (_logger.IsDebug) _logger.Debug($"Session with beacon chain peer {address} closed before identify completed: {DescribeFailure(e)}");
                return new DialOutcome(false, BeaconP2P.RemotePeerIdOf(session)?.ToString());
            }

            bool admitted = await AdmitSessionAsync(address, peerId, session, info, enr, token, dial);
            admissionResolved = true;
            return new DialOutcome(admitted);
        }
        catch (Libp2pException e) when (!token.IsCancellationRequested)
        {
            // The dial throws this when its connection closed before a session formed, which is how our side of a
            // simultaneous dial can end, and also when no connection opened at all.
            if (_logger.IsDebug) _logger.Debug($"Connection to beacon chain peer {address} closed before a session was established: {DescribeFailure(e)}");
            return new DialOutcome(false, Multiaddress.Decode(address).GetPeerId()?.ToString());
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            if (_logger.IsDebug) _logger.Debug($"Failed to connect to beacon chain peer {address}: {DescribeFailure(e)}");
            return new DialOutcome(false);
        }
        finally
        {
            _outboundDialGate.Release();

            // Covers the dial-timeout cancellation exit as well as a thrown status exchange: either
            // way the dial produced a session that no admission decision ever closed.
            if (session is not null && !admissionResolved)
            {
                await DisconnectUnadmittedAsync(session);
            }
        }
    }

    /// <summary>Status-exchanges an established session and, when it is on our fork, records it under
    /// <paramref name="address"/>. Shared by every admission path; the caller holds the reservation.</summary>
    /// <remarks>Counts the admission as in flight for its session until it returns, so a concurrent admission of the
    /// same session that fails does not tear the session down under this one (see <see cref="DisconnectUnadmittedAsync"/>).</remarks>
    /// <param name="dial">The outcome record of the outbound dial that opened the session; <c>null</c> for a session the remote opened.</param>
    private async Task<bool> AdmitSessionAsync(string address, string peerId, ISession session, BeaconP2P.SessionInfo info, string? enr, CancellationToken token, DialRecord? dial = null)
    {
        lock (_admissionLock)
        {
            CollectionsMarshal.GetValueRefOrAddDefault(_admittingSessions, session, out _)++;
        }

        try
        {
            return await ExchangeStatusAndRecordAsync(address, peerId, session, info, enr, token, dial);
        }
        finally
        {
            lock (_admissionLock)
            {
                if (--_admittingSessions[session] == 0)
                {
                    _admittingSessions.Remove(session);
                }
            }
        }
    }

    private async Task<bool> ExchangeStatusAndRecordAsync(string address, string peerId, ISession session, BeaconP2P.SessionInfo info, string? enr, CancellationToken token, DialRecord? dial)
    {
        ManagedPeer peer = new(this, _p2p, address, peerId, session, info.Direction, info.AgentVersion, enr) { Dial = dial };
        if (!await UpdateStatusAsync(peer, token))
        {
            return false;
        }

        // The ceiling reads _peers.Count under the same lock.
        lock (_admissionLock)
        {
            _peers[address] = peer;
            Metrics.BeaconChainPeerCount = _peers.Count;
        }

        Interlocked.Increment(ref Metrics.PeersConnectedCount);
        if (!RemoveWhenSessionCloses(peer))
        {
            return false;
        }

        if (_logger.IsInfo) _logger.Info($"Connected to beacon chain peer {address} ({info.Direction.ToString().ToLowerInvariant()}, head slot {peer.HeadSlot})");
        await RefreshCustodyAsync(peer, token, AdmissionMetadataTimeout);
        // The dial then records a failure: a success would clear the backoff the close of a peer that broke the protocol set (see RecordClosedSession).
        if (peer.RemovedOnClose)
        {
            return false;
        }

        PeerAdmitted?.Invoke(peer);
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
            if (_logger.IsDebug) _logger.Debug($"Admitting beacon chain peer {address} failed: {DescribeFailure(e)}");
            await DisconnectUnadmittedAsync(session);
        }
    }

    /// <summary>Closes a session whose admission failed after it was open. Left alone, the libp2p layer
    /// would keep a connection this manager neither counts against the band nor health-checks.</summary>
    private async Task DisconnectUnadmittedAsync(ISession session)
    {
        // A failed inbound admission frees its reservation before this runs, so a later admission may hold the session (see AdmitSessionAsync).
        lock (_admissionLock)
        {
            if (HoldsSession(session) || _admittingSessions.ContainsKey(session))
            {
                return;
            }
        }

        try
        {
            await session.DisconnectAsync();
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Disconnect from {session.RemoteAddress} failed: {DescribeFailure(e)}");
        }
    }

    /// <summary>Sends <c>goodbye</c> and disconnects a session that was never admitted. Noted in the peer
    /// id's disconnect history only when it already has one, so a banned peer that keeps knocking is
    /// visible in the diagnostics while a never-admitted id earns no record.</summary>
    private async Task RefuseSessionAsync(ISession session, string peerId, ulong reason, string detail)
    {
        if (_logger.IsDebug) _logger.Debug($"Refusing beacon chain peer: {detail}");
        RecordRefusal(peerId, reason, detail);
        await _p2p.GoodbyeAsync(session, reason, CancellationToken.None);
        try
        {
            await session.DisconnectAsync();
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Disconnect from {session.RemoteAddress} failed: {DescribeFailure(e)}");
        }
    }

    /// <remarks>
    /// A timeout during which no request of ours to any peer was answered is not counted (see <see cref="ManagedPeer.ExchangeAsync{T}"/>).
    /// At or below <see cref="IBeaconChainConfig.MinPeerCount"/> connected peers a peer that only times out is kept, out of selection at its failure limit,
    /// unless its session cannot open a channel at all: reconnecting is then the only way to reach it again.
    /// </remarks>
    private async Task CheckHealthAsync(ManagedPeer peer, CancellationToken token)
    {
        long startedAt = _timestamper.UtcNow.Ticks;
        try
        {
            if (!await UpdateStatusAsync(peer, token))
            {
                return;
            }

            ulong metadataSeqNumber = await peer.ExchangeAsync(RequestName.Ping, _ => _p2p.PingAsync(peer.Session, token), token);
            peer.RecordMessageSent();
            peer.RecordActivity();
            peer.ResetHealthCheckFailures();
            if (peer.MetadataSeqNumber != metadataSeqNumber)
            {
                await RefreshCustodyAsync(peer, token);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            await HandleHealthFailureAsync(peer, e, startedAt, token);
        }
    }

    internal async Task HandleHealthFailureAsync(IBeaconSyncPeer failedPeer, Exception e, long startedAt, CancellationToken token)
    {
        if (e is OperationCanceledException && token.IsCancellationRequested) return;
        ManagedPeer peer = (ManagedPeer)failedPeer;
        // A closed session removes the peer (see RemoveClosedSession) and is never a failure of it; a bad reply sent before it closed still counts.
        if (peer.IsSessionClosed)
        {
            if (!IsSilence(e)) peer.RecordFailedHealthCheck(violation: true);
            return;
        }

        bool timeout = e is OperationCanceledException or TimeoutException;
        bool independentSuccess = false;
        foreach (KeyValuePair<string, ManagedPeer> other in _peers)
        {
            if (other.Value.PeerId != peer.PeerId && other.Value.LastRequestServedAt > startedAt)
            {
                independentSuccess = true;
                break;
            }
        }

        bool protectedTimeout = timeout && !peer.ViolatedProtocolSinceLastHealthyCheck
            && e is not ReqRespTimeoutException { ChannelNeverOpened: true, NotBlamed: false };
        if (protectedTimeout)
        {
            peer.DeprioritiseAfterTimeout();
            if (!independentSuccess || e is ReqRespTimeoutException { NotBlamed: true })
            {
                peer.ResetTimeoutFailures();
                return;
            }
        }
        else if (e is ReqRespTimeoutException { NotBlamed: true })
        {
            return;
        }

        int failures = protectedTimeout ? peer.RecordTimeoutFailure() : peer.RecordFailedHealthCheck(violation: !IsSilence(e));
        if (_logger.IsDebug) _logger.Debug($"Beacon chain peer {peer.Id} failed health check ({failures}/{MaxConsecutiveFailures}): {DescribeFailure(e)}");
        if (failures >= MaxConsecutiveFailures)
        {
            await DropAsync(peer, GoodbyeReason.Fault, $"repeated failures, last: {DescribeFailure(e)}", token,
                unresponsive: IsUnresponsiveFailure(peer, e), healthFailure: e);
        }
    }

    /// <summary>Whether a peer at its failure limit for <paramref name="e"/> stays connected with <paramref name="connected"/> peers in the pool.</summary>
    /// <param name="e">The request failure.</param>
    /// <param name="connected">The number of connected peers.</param>
    /// <param name="violated">A reply since the last passing health check failed a content check, so the peer did more than time out.</param>
    internal bool IsKeptAtPeerFloor(Exception e, int connected, bool violated) =>
        connected <= _config.MinPeerCount
        && !violated
        && e is TimeoutException or OperationCanceledException
        && e is not ReqRespTimeoutException { ChannelNeverOpened: true };

    /// <summary>Reads the peer's custody group count from its <c>MetaData</c> v3 (fulu/p2p-interface.md); on failure the previous custody stands.</summary>
    /// <param name="timeout">Bounds the request; the request timeout when omitted.</param>
    private async Task RefreshCustodyAsync(ManagedPeer peer, CancellationToken token, TimeSpan? timeout = null)
    {
        try
        {
            MetaDataV3 metadata = await peer.ExchangeAsync(RequestName.MetaData, _ => _p2p.RequestMetaDataAsync(peer.Session, token, timeout), token);
            peer.RecordMessageSent();
            peer.ApplyMetadata(metadata);
        }
        catch (Exception e)
        {
            // Swallowed even on cancellation: the peer is already admitted, and the caller's next request observes the token.
            if (_logger.IsDebug) _logger.Debug($"Metadata request to beacon chain peer {peer.Id} failed: {DescribeFailure(e)}");
        }

        PublishCustodyShortfall();
    }

    /// <returns><c>false</c> when the peer was dropped for being on a different fork.</returns>
    private async Task<bool> UpdateStatusAsync(ManagedPeer peer, CancellationToken token)
    {
        StatusMessageV2 status;
        peer.StatusRequestStarted(_timestamper.UtcNowOffset.UtcTicks);
        try
        {
            status = await peer.ExchangeAsync(RequestName.Status, timing => _p2p.RequestStatusAsync(peer.Session, token, timing), token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            peer.StatusRequestFailed(_timestamper.UtcNowOffset.UtcTicks);
            throw;
        }
        finally
        {
            peer.StatusRequestEnded();
        }
        peer.RecordMessageSent();
        StatusMessageV2 local = _statusSource.CurrentStatus;
        if (!status.ForkDigest.AsSpan().SequenceEqual(local.ForkDigest))
        {
            await DropAsync(peer, GoodbyeReason.IrrelevantNetwork, "fork digest mismatch", token);
            return false;
        }

        if (ConflictsWithFinalized(status, local, _store, _spec?.SlotsPerEpoch ?? 0))
        {
            await DropAsync(peer, GoodbyeReason.IrrelevantNetwork, $"finalized checkpoint {status.FinalizedRoot} at epoch {status.FinalizedEpoch} is not in our chain", token);
            return false;
        }

        peer.SetStatus(status, _timestamper.UtcNowOffset.UtcTicks);
        peer.RecordActivity();
        return true;
    }

    /// <summary>Whether the peer's finalized checkpoint is proven absent from this node's chain at its epoch.</summary>
    /// <remarks>consensus-specs v1.7.0-beta.2 networking Status: disconnect only for a proven finalized conflict;
    /// future, genesis, and locally unknown checkpoints cannot prove one.</remarks>
    /// <param name="slotsPerEpoch">0 when no spec is known, which limits the check to our own finalized epoch.</param>
    internal static bool ConflictsWithFinalized(StatusMessageV2 remote, StatusMessageV2 local, BeaconChainStore? store, ulong slotsPerEpoch)
    {
        // Only the genesis checkpoint carries the zero root; a zero root at a later epoch is compared like any other.
        if (remote.FinalizedEpoch == 0 || remote.FinalizedRoot is not { } remoteRoot
            || local.FinalizedRoot is not { } localRoot || localRoot == Hash256.Zero || remote.FinalizedEpoch > local.FinalizedEpoch)
        {
            return false;
        }

        if (remote.FinalizedEpoch == local.FinalizedEpoch)
        {
            return remoteRoot != localRoot;
        }

        if (store is null || slotsPerEpoch == 0)
        {
            return false;
        }

        // consensus-specs v1.7.0-beta.2 get_block_root: use the latest block at or before the epoch's start slot.
        if (remote.FinalizedEpoch > ulong.MaxValue / slotsPerEpoch)
        {
            return false;
        }

        ulong startSlot = remote.FinalizedEpoch * slotsPerEpoch;
        if (store.GetCanonicalIndexTopSlot() is not { } topSlot || topSlot < startSlot)
        {
            return false;
        }

        ulong lowest = startSlot - Math.Min(startSlot, FinalizedCheckpointSearchEpochs * slotsPerEpoch);
        for (ulong slot = startSlot; ; slot--)
        {
            if (store.TryGetCanonicalRoot(slot, out Hash256? canonical))
            {
                return canonical != remoteRoot;
            }

            if (slot == lowest)
            {
                return false;
            }
        }
    }

    /// <summary>The cause of a failed peer request as an operator reads it: a timeout is named as one, not by its exception type.</summary>
    /// <remarks>A <see cref="ReqRespTimeoutException"/> already names the bound that fired and what the request was waiting for.</remarks>
    internal static string DescribeFailure(Exception e) =>
        e is not ReqRespTimeoutException and (OperationCanceledException or TimeoutException) ? "request timed out" : e.Message.Replace("Exception", "failure", StringComparison.Ordinal);

    /// <summary>The names requests go by in the per-request Debug line and in the concurrency cap, one per protocol ID whatever its fork.</summary>
    internal static class RequestName
    {
        public const string BlocksByRange = "Blocks-by-range";
        public const string BlocksByRoot = "Blocks-by-root";
        public const string ColumnsByRange = "Data-column-sidecars-by-range";
        public const string ColumnsByRoot = "Data-column-sidecars-by-root";
        public const string EnvelopesByRange = "Execution-payload-envelopes-by-range";
        public const string EnvelopesByRoot = "Execution-payload-envelopes-by-root";
        public const string Status = "Status";
        public const string Ping = "Ping";
        public const string MetaData = "MetaData";
    }

    /// <summary>A timeout or a lost session says nothing about the content the peer sends; any other failure of a health check is a bad reply.</summary>
    private static bool IsSilence(Exception e) =>
        e is OperationCanceledException or TimeoutException || PeerFailureClassifier.Classify(e) == PeerFailureReason.SessionClosed;

    /// <summary>Whether a drop after repeated failures shows only silence: the last failure is a timeout or a lost session and no reply in the run was a protocol violation.</summary>
    internal static bool IsUnresponsiveFailure(IBeaconSyncPeer peer, Exception e) =>
        IsSilence(e) && !((ManagedPeer)peer).ViolatedProtocolSinceLastHealthyCheck;

    /// <param name="unresponsive">The peer only stopped answering: the drop is recorded but earns no step toward a ban.</param>
    private async Task DropAsync(ManagedPeer peer, ulong reason, string detail, CancellationToken token, bool unresponsive = false, Exception? healthFailure = null)
    {
        bool kept;
        bool removed = false;
        lock (_admissionLock)
        {
            kept = healthFailure is not null && IsKeptAtPeerFloor(healthFailure, _peers.Count, peer.ViolatedProtocolSinceLastHealthyCheck);
            if (kept)
            {
                peer.HoldOutOfSelection();
            }
            else
            {
                // Only this peer: once its session closed, a newer peer can hold the same address.
                removed = _peers.TryRemove(new KeyValuePair<string, ManagedPeer>(peer.Id, peer));
                Metrics.BeaconChainPeerCount = _peers.Count;
            }
        }

        if (kept)
        {
            if (_logger.IsDebug) _logger.Debug($"Keeping beacon chain peer {peer.Id} out of selection instead of dropping it: {_peers.Count} peers connected, at most {_config.MinPeerCount}");
            return;
        }

        // A drop that began before the peer's session closed finds it removed and recorded by RemoveClosedSession already, with no session left to say goodbye on.
        if (!removed && peer.RemovedOnClose)
        {
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Dropping beacon chain peer {peer.Id}: {detail}");
        Interlocked.Increment(ref Metrics.PeersDroppedCount);
        Metrics.BeaconChainPeersDroppedByReason.Increment(new StringLabel(GoodbyeReasonName(reason)));
        if (removed) _dialHistory.Record(peer.Id, connected: false);
        RecordDisconnect(peer, reason, detail, unresponsive);
        await _p2p.GoodbyeAsync(peer.Session, reason, token);
        try
        {
            await peer.Session.DisconnectAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Disconnect from {peer.Id} failed: {DescribeFailure(e)}");
        }
    }

    /// <summary>Has <paramref name="peer"/>, just recorded in the pool, removed from it as soon as its libp2p session closes.</summary>
    /// <returns><c>false</c> when the session had already closed, so the peer is no longer in the pool.</returns>
    private bool RemoveWhenSessionCloses(ManagedPeer peer)
    {
        _closedPeers.TryRemove(peer.PeerId, out _);
        // Runs the removal at once when the session closed before this call; the registration ends with the session's token.
        _p2p.SessionClosedToken(peer.Session).Register(static state =>
        {
            (PeerManager manager, ManagedPeer closed) = ((PeerManager, ManagedPeer))state!;
            manager.RemoveClosedSession(closed);
        }, (this, peer));
        return _peers.TryGetValue(peer.Id, out ManagedPeer? held) && ReferenceEquals(held, peer);
    }

    /// <summary>Removes a peer whose libp2p session closed and wakes <see cref="WaitForAdmissionCapacityAsync"/>, so the dial loop can replace it.</summary>
    /// <remarks>
    /// A lost session is not a fault of the peer or its address: no failure is counted, its fault-disconnect streak is left as it is and its address gets no dial backoff.
    /// A peer that sent a reply failing a content check since its last passing health check is the exception: its close is a fault disconnect,
    /// so closing and reconnecting does not escape a ban (see <see cref="RecordClosedSession"/>).
    /// Its waiting and running requests end as disconnects on the same session token (see <see cref="ManagedPeer.ExchangeAsync{T}"/>).
    /// </remarks>
    private void RemoveClosedSession(ManagedPeer peer)
    {
        bool removed;
        lock (_admissionLock)
        {
            removed = _peers.TryRemove(new KeyValuePair<string, ManagedPeer>(peer.Id, peer));
            if (removed)
            {
                peer.MarkRemovedOnClose();
                RememberClosed(peer);
            }

            Metrics.BeaconChainPeerCount = _peers.Count;
        }

        if (!removed)
        {
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Beacon chain peer {peer.Id} disconnected: {SessionClosedDetail}");
        Interlocked.Increment(ref Metrics.PeersDroppedCount);
        Metrics.BeaconChainPeersDroppedByReason.Increment(new StringLabel(SessionClosedLabel));
        peer.RecordClose();
        WakeAdmissionWaiters();
        PublishCustodyShortfall();
    }

    /// <summary>At most this many peers removed for a closed session are kept findable by id, the newest; a churn of identities cannot grow it further.</summary>
    internal int ClosedPeerCapacity => Math.Max(1, 2 * _config.MaxPeerCount);

    /// <summary>Keeps <paramref name="peer"/>, just removed for its closed session, findable by id for <see cref="ClosedPeerWindow"/>, and forgets the ones past the window or the capacity.</summary>
    /// <remarks>Called under <c>_admissionLock</c>, the only writer of the order queue.</remarks>
    private void RememberClosed(ManagedPeer peer)
    {
        long now = _timestamper.UtcNowOffset.UtcTicks;
        long sequence = ++_closedSequence;
        _closedPeers[peer.PeerId] = (peer, now, sequence);
        _closedOrder.Enqueue((peer.PeerId, now, sequence));
        while (_closedOrder.TryPeek(out (string PeerId, long RemovedAtTicks, long Sequence) oldest)
            && (_closedOrder.Count > ClosedPeerCapacity || now - oldest.RemovedAtTicks > ClosedPeerWindow.Ticks))
        {
            _closedOrder.Dequeue();
            // Only the entry this queue item wrote: the same id may have closed again since.
            if (_closedPeers.TryGetValue(oldest.PeerId, out (ManagedPeer Peer, long RemovedAtTicks, long Sequence) entry) && entry.Sequence == oldest.Sequence)
            {
                _closedPeers.TryRemove(new KeyValuePair<string, (ManagedPeer Peer, long RemovedAtTicks, long Sequence)>(oldest.PeerId, entry));
            }
        }
    }

    private bool TryFindClosed(string peerId, out ManagedPeer? peer)
    {
        peer = _closedPeers.TryGetValue(peerId, out (ManagedPeer Peer, long RemovedAtTicks, long Sequence) closed)
            && _timestamper.UtcNowOffset.UtcTicks - closed.RemovedAtTicks <= ClosedPeerWindow.Ticks ? closed.Peer : null;
        return peer is not null;
    }

    internal int ClosedPeerCountForTest => _closedPeers.Count;

    /// <summary>Records the close of a peer's session in its peer id's history; as a fault disconnect when <paramref name="fault"/>, which also backs its address off.</summary>
    /// <param name="newDisconnect">The close was not recorded yet; <c>false</c> when a violation reported after the removal turns it into a fault.</param>
    private void RecordClosedSession(ManagedPeer peer, bool fault, bool newDisconnect)
    {
        if (fault)
        {
            if (peer.Dial is { } dial)
            {
                dial.Fail();
            }
            else
            {
                _dialHistory.Record(peer.Id, connected: false);
            }
        }

        RecordDisconnect(peer.PeerId, peer.MessagesSent, peer.FailuresReported, fault ? GoodbyeReason.Fault : SessionClosedReason, SessionClosedDetail, unresponsive: !fault, newDisconnect);
    }

    /// <summary>
    /// Folds a dropped session's counters into its peer id's durable record, and bans the id once it
    /// has caused <see cref="IBeaconChainConfig.FaultDisconnectsBeforeBan"/> consecutive fault
    /// disconnects; the ban lasts <see cref="IBeaconChainConfig.PeerBanMinutes"/>. A non-fault disconnect (fork rotation,
    /// our own shutdown) resets that streak: it is not evidence of misbehaviour, and BPO digest rotations legitimately cause
    /// fork-mismatch drops of otherwise-healthy peers. A drop for silence alone leaves the streak as it is.
    /// </summary>
    private void RecordDisconnect(ManagedPeer peer, ulong reason, string detail, bool unresponsive) =>
        RecordDisconnect(peer.PeerId, peer.MessagesSent, peer.FailuresReported, reason, detail, unresponsive);

    /// <summary>
    /// The session-independent half of <see cref="DropAsync"/>'s bookkeeping. Internal so a test can
    /// drive the ban/diagnostics state machine directly against real peer ids without standing up a
    /// live libp2p session for every one of <see cref="IBeaconChainConfig.FaultDisconnectsBeforeBan"/>
    /// fault disconnects.
    /// </summary>
    /// <param name="newDisconnect">Counts a disconnect; <c>false</c> when this one was recorded already and only its reason changes.</param>
    internal void RecordDisconnect(string peerId, long messagesSent, long failuresReported, ulong reason, string detail, bool unresponsive = false, bool newDisconnect = true)
    {
        DateTimeOffset now = _timestamper.UtcNowOffset;
        BanRecord record = GetOrCreateRecord(peerId);
        record.LastDisconnectReason = GoodbyeReasonName(reason);
        record.LastDisconnectDetail = detail;
        record.MessagesSent = messagesSent;
        record.FailuresReported = failuresReported;
        if (newDisconnect)
        {
            Interlocked.Increment(ref record.DisconnectCount);
        }

        if (unresponsive)
        {
            return;
        }

        int consecutiveFaults;
        lock (record)
        {
            // Lifts a ban that has run out under the same lock, so the streak below starts from zero.
            bool banned = IsBanActive(record, now);
            if (reason != GoodbyeReason.Fault)
            {
                record.ConsecutiveFaultDisconnects = 0;
                return;
            }

            consecutiveFaults = ++record.ConsecutiveFaultDisconnects;
            if (consecutiveFaults < _config.FaultDisconnectsBeforeBan || banned)
            {
                return;
            }

            record.BannedUntilTicks = (now + BanDuration).UtcTicks;
        }

        if (_logger.IsWarn) _logger.Warn($"Banned beacon chain peer {peerId} after {consecutiveFaults} consecutive fault disconnects");
    }

    /// <summary>
    /// Notes a refusal in an existing record only. Creating one for a never-admitted id would let a
    /// flood of distinct knockers at the ceiling evict the history of real peers, and a refusal is not
    /// evidence about the peer's behaviour either way, so the consecutive-fault streak is left as is.
    /// </summary>
    private void RecordRefusal(string peerId, ulong reason, string detail)
    {
        if (!_peerRecords.TryGetValue(peerId, out BanRecord? record))
        {
            return;
        }

        record.LastDisconnectReason = GoodbyeReasonName(reason);
        record.LastDisconnectDetail = detail;
        Interlocked.Increment(ref record.DisconnectCount);
    }

    /// <summary>Internal so a test can assert ban state without dialing: see <see cref="RecordDisconnect(string,long,long,ulong,string,bool)"/>.</summary>
    internal bool IsBannedForTest(string peerId) => IsBanned(peerId);

    internal static long MessagesSentForTest(IBeaconSyncPeer peer) => ((ManagedPeer)peer).MessagesSent;

    /// <summary>Internal so a test can hold a peer's requests open on a session it controls, without a status exchange.</summary>
    internal IBeaconSyncPeer AddPeerForTest(ISession session, string address, StatusMessageV2? status = null)
    {
        ManagedPeer peer = new(this, _p2p, address, ExtractPeerId(address), session, PeerDirection.Outbound, null, null);
        if (status is not null)
        {
            peer.SetStatus(status, _timestamper.UtcNowOffset.UtcTicks);
        }
        _peers[address] = peer;
        RemoveWhenSessionCloses(peer);
        return peer;
    }

    /// <summary>Internal so a test can run a drop that began before the peer's session closed, as the band trim can.</summary>
    internal Task DropForTest(IBeaconSyncPeer peer, ulong reason, string detail) => DropAsync((ManagedPeer)peer, reason, detail, CancellationToken.None);

    internal static int RequestsInFlightForTest(IBeaconSyncPeer peer) => ((ManagedPeer)peer).RequestsInFlight;

    internal static int ConsecutiveFailuresForTest(IBeaconSyncPeer peer) => ((ManagedPeer)peer).ConsecutiveFailures;

    /// <summary>Internal so a test with one silent peer can count its timeouts, which this node could not tell from its own stall.</summary>
    internal void CountEveryTimeoutForTest() => Volatile.Write(ref _lastAnswerAt, long.MaxValue);

    internal static long FailuresReportedForTest(IBeaconSyncPeer peer) => ((ManagedPeer)peer).FailuresReported;

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

        /// <summary>UTC ticks at which the ban ends; 0 when the id is not banned.</summary>
        public long BannedUntilTicks;
        public int ConsecutiveFaultDisconnects;
        public int DisconnectCount;
        public long MessagesSent;
        public long FailuresReported;
        public string? LastDisconnectReason;
        public string? LastDisconnectDetail;
    }

    private sealed class ManagedPeer(PeerManager manager, BeaconP2P p2p, string address, string peerId, ISession session, PeerDirection direction, string? agentVersion, string? enr) : IBeaconSyncPeer
    {
        private readonly object _requestFailureLock = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestSlots = new(StringComparer.Ordinal);
        private int _requestsInFlight;
        private int _unblamedFailures;
        private int _unblamedInARow;
        private volatile bool _heldOutOfSelection;
        private int _requestFailures;
        // Set by a protocol violation and kept until the request failures it added are all forgiven, which a passing health check does not do.
        private bool _requestFailuresIncludeViolation;
        private DateTimeOffset _requestFailuresDecayFrom;
        private long _cooldownUntilTicks;
        private int _consecutiveFailures;
        private bool _violatedProtocol;
        private long _lastRequestServedAt;
        private int _timeoutFailures;
        // A passing health check clears only the selection penalty from health timeouts, never failed sync requests.
        private int _healthTimeoutFailures;
        private long _messagesSent;
        private long _failuresReported;
        private volatile PeerColumnCustody _custody = CustodyOf(session, PeerColumnCustody.CustodyGroupCountOf(enr));
        private volatile StatusMessageV2? _status;
        private long _statusReceivedTicks;
        private long _statusFailedTicks;
        private long _statusRequestedTicks;
        private int _statusRequestsInFlight;
        private int _statusRefreshClaimed;
        private readonly Lock _closeLock = new();
        private bool _removedOnClose;
        private bool _closeRecorded;
        private bool _closeCountedAsFault;

        public ISession Session { get; } = session;

        /// <summary>The outbound dial that admitted this peer; <c>null</c> for a session the remote opened.</summary>
        public DialRecord? Dial { get; init; }
        public StatusMessageV2? Status => _status;

        /// <summary>Whether the libp2p layer has dropped <see cref="Session"/>; every further request to it fails.</summary>
        public bool IsSessionClosed => p2p.SessionClosedToken(Session).IsCancellationRequested;

        /// <summary>UTC ticks at which the current <see cref="Status"/> was received.</summary>
        public long StatusReceivedTicks => Interlocked.Read(ref _statusReceivedTicks);

        public void SetStatus(StatusMessageV2 status, long nowTicks)
        {
            _status = status;
            Interlocked.Exchange(ref _statusReceivedTicks, nowTicks);
        }

        public void StatusRequestStarted(long nowTicks)
        {
            Interlocked.Increment(ref _statusRequestsInFlight);
            Interlocked.Exchange(ref _statusRequestedTicks, nowTicks);
        }

        public void StatusRequestEnded() => Interlocked.Decrement(ref _statusRequestsInFlight);

        public void StatusRequestFailed(long nowTicks) => Interlocked.Exchange(ref _statusFailedTicks, nowTicks);

        /// <summary>Whether the current status was received before <paramref name="sinceTicks"/> and a request for a newer one failed since.</summary>
        public bool IsStatusStaleSince(long sinceTicks) =>
            StatusReceivedTicks < sinceTicks && Interlocked.Read(ref _statusFailedTicks) >= sinceTicks;

        /// <summary>Claims a <c>status</c> refresh unless one is claimed or outstanding, or one was started within <paramref name="minIntervalTicks"/>; one caller wins a race.</summary>
        public bool TryClaimStatusRefresh(long nowTicks, long minIntervalTicks)
        {
            if (Volatile.Read(ref _statusRequestsInFlight) != 0
                || nowTicks - Interlocked.Read(ref _statusRequestedTicks) < minIntervalTicks
                || Interlocked.CompareExchange(ref _statusRefreshClaimed, 1, 0) != 0)
            {
                return false;
            }

            Interlocked.Exchange(ref _statusRequestedTicks, nowTicks);
            return true;
        }

        /// <summary>Ends a refresh <see cref="TryClaimStatusRefresh"/> claimed, whether it ran, failed or was cancelled while queued.</summary>
        public void ReleaseStatusRefresh() => Volatile.Write(ref _statusRefreshClaimed, 0);

        /// <summary>The libp2p peer id this session was dialed as (see <see cref="PeerManager.ExtractPeerId"/>).</summary>
        public string PeerId { get; } = peerId;

        /// <summary>Which side opened the session, as the libp2p layer saw it happen.</summary>
        public PeerDirection Direction { get; } = direction;

        /// <summary>The identify agent string, <c>null</c> when the peer left the probe unanswered.</summary>
        public string? AgentVersion { get; } = agentVersion;

        /// <summary>The discv5 ENR text this peer was discovered with; <c>null</c> for a static peer or
        /// an inbound session (see <see cref="PeerManager.TryAddPeerAsync"/>'s optional parameter).</summary>
        public string? Enr { get; } = enr;

        public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

        /// <summary>The selection penalty from failed requests; a passing health check clears only its health-timeout contribution.</summary>
        /// <remarks>Each served request takes one off, and one is forgiven every <see cref="RequestFailureDecayInterval"/> since the latest failure. Held at the limit, so a peer taken out of selection needs one served request or one interval to return.</remarks>
        public int RequestFailures
        {
            get
            {
                lock (_requestFailureLock)
                {
                    DecayRequestFailures();
                    return _requestFailures + _healthTimeoutFailures;
                }
            }
        }

        public bool IsAtFailureLimit => _heldOutOfSelection || RequestFailures >= MaxConsecutiveFailures;

        /// <summary>Whether the peer may still be offered while every peer is at the request-failure limit: not held out of selection, its session open, and no invalid data behind its penalty.</summary>
        public bool IsOfferableAtFailureLimit
        {
            get
            {
                if (_heldOutOfSelection || ViolatedProtocolSinceLastHealthyCheck || p2p.SessionClosedToken(Session).IsCancellationRequested)
                {
                    return false;
                }

                lock (_requestFailureLock)
                {
                    DecayRequestFailures();
                    return !_requestFailuresIncludeViolation;
                }
            }
        }

        /// <summary>Keeps a peer that failed its health checks out of selection until one passes, when it is not dropped (see <see cref="IsKeptAtPeerFloor"/>).</summary>
        public void HoldOutOfSelection() => _heldOutOfSelection = true;

        /// <summary>Whether a request of ours failed within <see cref="RequestFailureCooldown"/> of <paramref name="nowTicks"/>. Unlike <see cref="RequestFailures"/>, a served request does not end it.</summary>
        public bool IsCoolingDown(long nowTicks) => nowTicks < Volatile.Read(ref _cooldownUntilTicks);

        /// <summary>A reply since the last passing health check failed a content check, which a timeout in the same run does not excuse.</summary>
        public bool ViolatedProtocolSinceLastHealthyCheck => Volatile.Read(ref _violatedProtocol);

        public void ResetHealthCheckFailures()
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            ResetTimeoutFailures();
            lock (_requestFailureLock)
            {
                _healthTimeoutFailures = 0;
            }
            Volatile.Write(ref _violatedProtocol, false);
            _heldOutOfSelection = false;
        }

        public void RecordRequestServed()
        {
            RecordActivity();
            lock (_requestFailureLock)
            {
                DecayRequestFailures();
                if (_healthTimeoutFailures > 0)
                {
                    _healthTimeoutFailures--;
                }
                else if (_requestFailures > 0)
                {
                    _requestFailures--;
                    _requestFailuresIncludeViolation &= _requestFailures > 0;
                }
            }
        }

        /// <summary>Offers the peer after others as a failed request does, without a step toward <see cref="MaxConsecutiveFailures"/>.</summary>
        public void DeprioritiseAfterTimeout()
        {
            Volatile.Write(ref _cooldownUntilTicks, (manager._timestamper.UtcNowOffset + RequestFailureCooldown).UtcTicks);
            lock (_requestFailureLock)
            {
                DecayRequestFailures();
                _requestFailuresDecayFrom = manager._timestamper.UtcNowOffset;
                _healthTimeoutFailures = Math.Min(MaxConsecutiveFailures - _requestFailures, _healthTimeoutFailures + 1);
            }
        }

        public void RecordActivity() => Interlocked.Exchange(ref _lastRequestServedAt, manager._timestamper.UtcNow.Ticks);
        public long LastRequestServedAt => Interlocked.Read(ref _lastRequestServedAt);
        public void ResetTimeoutFailures() => Interlocked.Exchange(ref _timeoutFailures, 0);
        public int RecordTimeoutFailure() => Interlocked.Increment(ref _timeoutFailures);

        private void AddRequestFailures(int failures, bool violation = false)
        {
            lock (_requestFailureLock)
            {
                DecayRequestFailures();
                _requestFailuresIncludeViolation |= violation;
                _requestFailuresDecayFrom = manager._timestamper.UtcNowOffset;
                _requestFailures = Math.Min(MaxConsecutiveFailures, _requestFailures + failures);
                _healthTimeoutFailures = Math.Min(_healthTimeoutFailures, MaxConsecutiveFailures - _requestFailures);
            }
        }

        private void DecayRequestFailures()
        {
            if (_requestFailures + _healthTimeoutFailures == 0)
            {
                return;
            }

            DateTimeOffset now = manager._timestamper.UtcNowOffset;
            long forgiven = (now - _requestFailuresDecayFrom).Ticks / RequestFailureDecayInterval.Ticks;
            if (forgiven > 0)
            {
                long healthForgiven = Math.Min(_healthTimeoutFailures, forgiven);
                _healthTimeoutFailures -= (int)healthForgiven;
                _requestFailures = (int)Math.Max(0, _requestFailures - (forgiven - healthForgiven));
                _requestFailuresIncludeViolation &= _requestFailures > 0;
                _requestFailuresDecayFrom += TimeSpan.FromTicks(forgiven * RequestFailureDecayInterval.Ticks);
            }
        }

        /// <returns>The consecutive failures including this one.</returns>
        public int RecordFailedHealthCheck(bool violation = false)
        {
            if (violation)
            {
                RecordViolation();
            }

            return Interlocked.Increment(ref _consecutiveFailures);
        }

        /// <summary>Whether this peer left the pool because its session closed (see <see cref="PeerManager.RemoveClosedSession"/>).</summary>
        public bool RemovedOnClose
        {
            get
            {
                lock (_closeLock)
                {
                    return _removedOnClose;
                }
            }
        }

        /// <summary>Marks the peer as removed for its closed session; called under the pool's admission lock with the removal, so a drop that misses the peer sees why.</summary>
        public void MarkRemovedOnClose()
        {
            lock (_closeLock)
            {
                _removedOnClose = true;
            }
        }

        /// <summary>Records the close in the peer id's history, as a fault disconnect when a reply failed a content check since the last passing health check.</summary>
        public void RecordClose()
        {
            // Under the same lock as RecordViolation, so a violation is either seen here or turns this record into a fault afterwards.
            lock (_closeLock)
            {
                _closeRecorded = true;
                _closeCountedAsFault = ViolatedProtocolSinceLastHealthyCheck;
                manager.RecordClosedSession(this, _closeCountedAsFault, newDisconnect: true);
            }
        }

        // A violation reported after the close was recorded still turns that close into a fault disconnect, once.
        private void RecordViolation()
        {
            lock (_closeLock)
            {
                Volatile.Write(ref _violatedProtocol, true);
                if (_closeRecorded && !_closeCountedAsFault)
                {
                    _closeCountedAsFault = true;
                    manager.RecordClosedSession(this, fault: true, newDisconnect: false);
                }
            }
        }

        public long MessagesSent => Interlocked.Read(ref _messagesSent);
        public long FailuresReported => Interlocked.Read(ref _failuresReported);

        public string Id => address;
        public ulong HeadSlot => Status?.HeadSlot ?? 0;
        public ulong EarliestAvailableSlot => Status?.EarliestAvailableSlot ?? 0;

        /// <summary>Until <c>MetaData</c> answers, the ENR's <c>cgc</c> when this peer was discovered, else the <c>CUSTODY_REQUIREMENT</c> floor.</summary>
        public PeerColumnCustody Custody => _custody;

        /// <summary>The <c>seq_number</c> of the last <c>MetaData</c> applied; <c>null</c> before the first.</summary>
        public ulong? MetadataSeqNumber { get; private set; }

        public void ApplyMetadata(MetaDataV3 metadata)
        {
            MetadataSeqNumber = metadata.SeqNumber;
            _custody = CustodyOf(Session, metadata.CustodyGroupCount);
        }

        private static PeerColumnCustody CustodyOf(ISession session, ulong? custodyGroupCount) =>
            PeerColumnCustody.NodeIdOf(BeaconP2P.RemotePublicKeyOf(session)) is { } nodeId ? PeerColumnCustody.ForNode(nodeId, custodyGroupCount) : PeerColumnCustody.None;

        public void RecordMessageSent() => Interlocked.Increment(ref _messagesSent);

        /// <summary>Requests of ours to this peer that are waiting for a slot or running.</summary>
        public int RequestsInFlight => Volatile.Read(ref _requestsInFlight);

        /// <summary>Runs one request to this peer once it holds one of the peer's slots for <paramref name="protocol"/>, and logs its timing at Debug when it ends.</summary>
        /// <remarks>Networking req/resp requesting side limits each protocol to two concurrent requests; a cancelled dial retains its permit until the protocol ends.
        /// A timeout with no other answer is excused for at most eight consecutive attempts, so a wedged peer can still be reconnected.</remarks>
        internal async Task<T> ExchangeAsync<T>(string protocol, Func<RequestTiming, Task<T>> request, CancellationToken token)
        {
            long queuedAt = Stopwatch.GetTimestamp();
            SemaphoreSlim slots = _requestSlots.GetOrAdd(protocol, static _ => new SemaphoreSlim(ReqRespProtocolBase.MaxConcurrentRequests));
            Interlocked.Increment(ref _requestsInFlight);
            try
            {
                using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(token, p2p.SessionClosedToken(Session));
                await slots.WaitAsync(waiting.Token);
                // A slot freed as the session closes can be granted before the close is seen; the request still never left.
                if (p2p.SessionClosedToken(Session).IsCancellationRequested)
                {
                    slots.Release();
                    throw new OperationCanceledException(waiting.Token);
                }
            }
            catch (Exception e)
            {
                Interlocked.Decrement(ref _requestsInFlight);
                bool disconnected = e is OperationCanceledException && !token.IsCancellationRequested && p2p.SessionClosedToken(Session).IsCancellationRequested;
                string waitOutcome = disconnected
                    ? $"peer disconnected after {ReqRespProtocolBase.Seconds(Stopwatch.GetElapsedTime(queuedAt))} waiting for a request slot: its libp2p session closed"
                    : e is OperationCanceledException && token.IsCancellationRequested ? "cancelled by this node" : DescribeFailure(e);
                if (manager._logger.IsDebug) manager._logger.Debug($"{protocol} to {Id}: requests in flight {RequestsInFlight}, slot wait {Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds:F0} ms, request not sent, {waitOutcome}");
                if (disconnected)
                {
                    throw new IOException(waitOutcome, e);
                }

                throw;
            }

            long startedAt = Stopwatch.GetTimestamp();
            TimeSpan slotWait = Stopwatch.GetElapsedTime(queuedAt, startedAt);
            RequestTiming timing = new();
            string outcome = "served";
            try
            {
                T response = await request(timing);
                RecordActivity();
                Volatile.Write(ref manager._lastAnswerAt, Stopwatch.GetTimestamp());
                Volatile.Write(ref _unblamedInARow, 0);
                return response;
            }
            catch (Exception e)
            {
                outcome = e is OperationCanceledException && token.IsCancellationRequested ? "cancelled by this node" : DescribeFailure(e);
                if (WithoutPartialReply(e) is ReqRespTimeoutException timeout && Volatile.Read(ref manager._lastAnswerAt) < startedAt
                    && Interlocked.Increment(ref _unblamedInARow) <= MaxConsecutiveFailures)
                {
                    timeout.NotBlamed = true;
                    outcome += ", not counted against the peer as no request to any peer was answered meanwhile";
                }

                throw;
            }
            finally
            {
                if (manager._logger.IsDebug) manager._logger.Debug($"{protocol} to {Id}: requests in flight {RequestsInFlight}, slot wait {slotWait.TotalMilliseconds:F0} ms, {timing}, {outcome}");
                Task settled = timing.Settled();
                if (settled.IsCompleted)
                {
                    FreeSlot(slots);
                }
                else
                {
                    _ = settled.ContinueWith(_ => FreeSlot(slots), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }

        /// <summary>Takes the note left by a failed sync request that is not to be counted against this peer; its <see cref="ReportFailure(PeerFailureReason, string?, bool)"/> then does not count.</summary>
        /// <remarks>Sync reports a failure by its text only, so the note is per peer: with failures of the same peer reported out of order the count stays right but can land on the other failure.</remarks>
        public bool TryTakeUnblamedFailure()
        {
            int unblamed = Volatile.Read(ref _unblamedFailures);
            while (unblamed > 0)
            {
                int seen = Interlocked.CompareExchange(ref _unblamedFailures, unblamed - 1, unblamed);
                if (seen == unblamed)
                {
                    return true;
                }

                unblamed = seen;
            }

            return false;
        }

        private void FreeSlot(SemaphoreSlim slots)
        {
            slots.Release();
            Interlocked.Decrement(ref _requestsInFlight);
        }

        private async Task<T> Served<T>(string protocol, Func<RequestTiming, Task<T>> request, CancellationToken token)
        {
            RecordMessageSent();
            T response;
            try
            {
                response = await ExchangeAsync(protocol, request, token);
            }
            catch (Exception e) when (WithoutPartialReply(e) is ReqRespTimeoutException { NotBlamed: true })
            {
                Interlocked.Increment(ref _unblamedFailures);
                throw;
            }

            RecordRequestServed();
            return response;
        }

        /// <summary>The failure a partial reply carries, so a timeout after some chunks is judged as the timeout it is.</summary>
        private static Exception WithoutPartialReply(Exception e) =>
            e is PartialBlocksException or PartialSidecarsException ? e.InnerException ?? e : e;

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token) =>
            Served(RequestName.BlocksByRange, timing => p2p.RequestBlocksByRangeAsync(Session, startSlot, count, token, timing), token);

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token) =>
            Served(RequestName.BlocksByRoot, timing => p2p.RequestBlocksByRootAsync(Session, roots, token, timing), token);

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) =>
            Served(RequestName.ColumnsByRange, timing => p2p.RequestDataColumnSidecarsByRangeAsync(Session, startSlot, count, columns, token, timing), token);

        public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token) =>
            Served(RequestName.ColumnsByRoot, timing => p2p.RequestDataColumnSidecarsByRootAsync(Session, identifiers, token, timing), token);

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) =>
            Served(RequestName.ColumnsByRange, timing => p2p.RequestGloasDataColumnSidecarsByRangeAsync(Session, startSlot, count, columns, token, timing), token);

        public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token) =>
            Served(RequestName.ColumnsByRoot, timing => p2p.RequestGloasDataColumnSidecarsByRootAsync(Session, identifiers, token, timing), token);

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong startSlot, ulong count, CancellationToken token) =>
            Served(RequestName.EnvelopesByRange, timing => p2p.RequestExecutionPayloadEnvelopesByRangeAsync(Session, startSlot, count, token, timing), token);

        public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] roots, CancellationToken token) =>
            Served(RequestName.EnvelopesByRoot, timing => p2p.RequestExecutionPayloadEnvelopesByRootAsync(Session, roots, token, timing), token);

        public void ReportFailure(PeerFailureReason reason, string? detail = null) => ReportFailure(reason, detail, ownRequest: true);

        /// <param name="ownRequest">The failure is of a request this node sent, so the peer is offered after others for <see cref="RequestFailureCooldown"/>.</param>
        public void ReportFailure(PeerFailureReason reason, string? detail, bool ownRequest)
        {
            // A request that failed because the session closed is not the peer's fault; a bad reply it sent before still is.
            if (reason != PeerFailureReason.ProtocolViolation && IsSessionClosed)
            {
                if (manager._logger.IsDebug) manager._logger.Debug($"Beacon chain peer {Id} failed a request after its libp2p session closed, not counted against it{(detail is null ? "" : $" ({detail})")}");
                return;
            }

            Interlocked.Increment(ref _failuresReported);
            if (ownRequest)
            {
                Volatile.Write(ref _cooldownUntilTicks, (manager._timestamper.UtcNowOffset + RequestFailureCooldown).UtcTicks);
                if (reason == PeerFailureReason.RequestFailed && TryTakeUnblamedFailure())
                {
                    if (manager._logger.IsDebug) manager._logger.Debug($"Beacon chain peer {Id} failed a request, not counted as no request of ours to any peer was answered meanwhile{(detail is null ? "" : $" ({detail})")}");
                    return;
                }
            }

            // A dead session means every further request would fail, so skip the failure budget and
            // let the next maintenance round (or the dial-loop cooldown) reconnect instead of
            // wedging on a zombie session.
            int failures;
            if (reason == PeerFailureReason.SessionClosed)
            {
                failures = Interlocked.Exchange(ref _consecutiveFailures, MaxConsecutiveFailures);
                AddRequestFailures(MaxConsecutiveFailures);
            }
            else
            {
                failures = Interlocked.Increment(ref _consecutiveFailures);
                if (reason == PeerFailureReason.ProtocolViolation)
                {
                    RecordViolation();
                }

                // A violation takes back the credit the reply that carried it just earned.
                AddRequestFailures(reason == PeerFailureReason.ProtocolViolation ? 2 : 1, violation: reason == PeerFailureReason.ProtocolViolation);
            }

            Metrics.BeaconChainPeerFailuresByReason.Increment(new StringLabel(reason.ToString()));
            if (manager._logger.IsDebug) manager._logger.Debug($"Beacon chain peer {Id} reported as failing ({failures}/{MaxConsecutiveFailures}): {reason}{(detail is null ? "" : $" ({detail})")}");
        }

    }
}
