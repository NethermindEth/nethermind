// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Network.Contract.P2P;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Init.FlatHistory;

/// <summary>
/// Static-peer-friendly eligibility filter over <see cref="ISyncPeerPool.InitializedPeers"/> - no discovery/ENR
/// scoring, just "which connected peers advertised nhist1 capability that matches what I need". A peer that never
/// negotiated nhist1 at all has no <see cref="INHistSyncPeer"/> satellite protocol registered and is skipped
/// entirely; one that negotiated it but has not yet exchanged its <c>NHistStatusMessage</c> (an empty
/// <see cref="INHistSyncPeer.PeerServedScopes"/>) is treated as not-yet-useful rather than eligible.
/// </summary>
public sealed class NHistPeerSelector(ISyncPeerPool peerPool)
{
    public static readonly IReadOnlySet<PublicKey> NoExclusions = new HashSet<PublicKey>();

    public bool TryGetEligibleImportPeer(IReadOnlySet<PublicKey> excluded, out PeerInfo peer, out INHistSyncPeer syncPeer)
    {
        foreach (PeerInfo candidate in peerPool.InitializedPeers)
        {
            if (excluded.Contains(candidate.SyncPeer.Node.Id)) continue;
            if (!candidate.SyncPeer.TryGetSatelliteProtocol(Protocol.NHist, out INHistSyncPeer handler)) continue;
            if (handler.PeerServedScopes.Length == 0) continue;

            peer = candidate;
            syncPeer = handler;
            return true;
        }

        peer = null!;
        syncPeer = null!;
        return false;
    }

    /// <summary>A windowed peer (one that only serves a bounded retention window) is a valid import source but is
    /// never eligible here: <paramref name="requiredRowFormatVersion"/> exists precisely so a format mismatch -
    /// which would otherwise silently corrupt a byte-identical clone - is caught by peer selection, before
    /// <see cref="Nethermind.State.Flat.History.ArchiveCloneImporter"/>'s own defense-in-depth check ever runs.</summary>
    public bool TryGetEligibleCloneSource(byte requiredRowFormatVersion, IReadOnlySet<PublicKey> excluded, out PeerInfo peer, out INHistSyncPeer syncPeer)
        => TryGetEligibleCloneSource(requiredRowFormatVersion, excluded, out peer, out syncPeer, null);

    public bool TryGetEligibleCloneSource(byte requiredRowFormatVersion, IReadOnlySet<PublicKey> excluded, out PeerInfo peer, out INHistSyncPeer syncPeer, Action<string>? skipDiagnostics)
    {
        foreach (PeerInfo candidate in peerPool.InitializedPeers)
        {
            if (excluded.Contains(candidate.SyncPeer.Node.Id)) continue;
            if (!candidate.SyncPeer.TryGetSatelliteProtocol(Protocol.NHist, out INHistSyncPeer handler))
            {
                skipDiagnostics?.Invoke($"peer {candidate.SyncPeer.Node:s} has no nhist satellite protocol registered");
                continue;
            }

            if (!handler.PeerSupportsFullClone)
            {
                skipDiagnostics?.Invoke($"peer {candidate.SyncPeer.Node:s} advertises SupportsFullClone=false (served scopes: {handler.PeerServedScopes.Length}, peer row format {handler.PeerRowFormatVersion})");
                continue;
            }

            if (handler.PeerRowFormatVersion != requiredRowFormatVersion)
            {
                skipDiagnostics?.Invoke($"peer {candidate.SyncPeer.Node:s} serves row format {handler.PeerRowFormatVersion} but this node requires {requiredRowFormatVersion}");
                continue;
            }

            peer = candidate;
            syncPeer = handler;
            return true;
        }

        peer = null!;
        syncPeer = null!;
        return false;
    }
}
