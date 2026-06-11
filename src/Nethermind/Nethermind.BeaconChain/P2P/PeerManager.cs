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
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// Maintains connections to the configured static peers: dials them, exchanges <c>status</c> and
/// <c>ping</c> periodically, and prunes peers on a fork digest mismatch or repeated failures.
/// </summary>
/// <remarks>Discovery-based peer acquisition arrives with discv5 in a later milestone.</remarks>
public class PeerManager(
    BeaconP2P p2p,
    IBeaconChainConfig config,
    IBeaconChainStatusSource statusSource,
    ILogManager logManager) : IBeaconSyncPeerPool
{
    private const int MaxConsecutiveFailures = 3;
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = logManager.GetClassLogger<PeerManager>();
    private readonly ConcurrentDictionary<string, ManagedPeer> _peers = new();

    public async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceRoundAsync(token);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (_logger.IsError) _logger.Error("Beacon chain peer maintenance failed.", e);
            }

            await Task.Delay(MaintenanceInterval, token);
        }
    }

    /// <summary>Connects missing static peers and re-exchanges status/ping with connected ones, pruning unhealthy peers.</summary>
    public async Task RunMaintenanceRoundAsync(CancellationToken token)
    {
        foreach (string address in StaticPeerAddresses())
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

    private string[] StaticPeerAddresses() =>
        config.StaticPeers?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private async Task ConnectAsync(string address, CancellationToken token)
    {
        try
        {
            ISession session = await p2p.DialPeerAsync(Multiaddress.Decode(address), token);
            ManagedPeer peer = new(this, p2p, address, session);
            if (!await UpdateStatusAsync(peer, token))
            {
                return;
            }

            _peers[address] = peer;
            if (_logger.IsInfo) _logger.Info($"Connected to beacon chain peer {address} (head slot {peer.HeadSlot})");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug($"Failed to connect to beacon chain peer {address}: {e.Message}");
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
            peer.ConsecutiveFailures = 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
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
        await p2p.GoodbyeAsync(peer.Session, reason, token);
        try
        {
            await peer.Session.DisconnectAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_logger.IsTrace) _logger.Trace($"Disconnect from {peer.Id} failed: {e.Message}");
        }
    }

    private sealed class ManagedPeer(PeerManager manager, BeaconP2P p2p, string address, ISession session) : IBeaconSyncPeer
    {
        private int _consecutiveFailures;

        public ISession Session { get; } = session;
        public StatusMessageV2? Status { get; set; }

        public int ConsecutiveFailures
        {
            get => _consecutiveFailures;
            set => _consecutiveFailures = value;
        }

        public string Id => address;
        public ulong HeadSlot => Status?.HeadSlot ?? 0;

        public Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token) =>
            p2p.RequestBlocksByRangeAsync(Session, startSlot, count, token);

        public void ReportFailure(string reason)
        {
            // Pruning happens on the next maintenance round once the threshold is reached.
            int failures = Interlocked.Increment(ref _consecutiveFailures);
            if (manager._logger.IsDebug) manager._logger.Debug($"Beacon chain peer {Id} reported as failing ({failures}/{MaxConsecutiveFailures}): {reason}");
        }
    }
}
