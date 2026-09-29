// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.P2P;

/// <summary>Closed-cardinality reasons a peer can be reported as failing, so the failure metric label
/// cannot grow without bound the way a free-text reason would.</summary>
public enum PeerFailureReason
{
    /// <summary>A blocks-by-range/by-root (or similar) request threw or timed out.</summary>
    RequestFailed,

    /// <summary>The peer returned content that fails a check the caller enforces, such as bad parent-root linkage.</summary>
    ProtocolViolation,

    /// <summary>The underlying session or channel is already gone; every further request would fail too.</summary>
    SessionClosed,
}

/// <summary>A connected, status-exchanged beacon chain peer usable by range sync.</summary>
public interface IBeaconSyncPeer
{
    string Id { get; }

    /// <summary>The head slot last advertised by the peer over <c>status</c>.</summary>
    ulong HeadSlot { get; }

    /// <summary>The <c>earliest_available_slot</c> of the peer's last Status v2; 0 when it sent none (Status v1).</summary>
    ulong EarliestAvailableSlot => 0;

    /// <summary>Each block has the SSZ shape of the fork its slot belongs to.</summary>
    Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token);

    /// <summary>Each block has the SSZ shape of the fork its slot belongs to.</summary>
    Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token);

    /// <summary>Fulu-shaped sidecars; the window must lie wholly before the Gloas fork.</summary>
    Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token);

    /// <summary>Fulu-shaped sidecars for the given block roots and columns.</summary>
    Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token);

    /// <summary>The columns this peer custodies; it is never asked for another (fulu/p2p-interface.md).</summary>
    PeerColumnCustody Custody { get; }

    /// <summary>Gloas-shaped sidecars; the window must lie wholly in Gloas epochs.</summary>
    Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token);

    /// <summary>Gloas-shaped sidecars for the given block roots and columns.</summary>
    Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token);

    Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong startSlot, ulong count, CancellationToken token);

    Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] roots, CancellationToken token);

    /// <summary>Records a protocol violation or failure with a closed-cardinality reason; repeated
    /// reports take the peer out of request selection until it serves a request or the failures decay.</summary>
    void ReportFailure(PeerFailureReason reason, string? detail = null);
}

/// <summary>The pool of sync-usable peers maintained by the peer manager.</summary>
public interface IBeaconSyncPeerPool
{
    /// <summary>Returns peers advertising a head at or past <paramref name="minHeadSlot"/>, best head first, leaving out peers whose requests keep failing (see <see cref="IBeaconSyncPeer.ReportFailure"/>).</summary>
    IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot);

    /// <summary>Raised once a peer is admitted and its custody is known, so a waiter can ask it without waiting for its next poll; never raised by a pool that does not implement it.</summary>
    event Action<IBeaconSyncPeer>? PeerAdmitted
    {
        add { }
        remove { }
    }
}

/// <summary>Connection direction of a tracked beacon chain peer.</summary>
public enum PeerDirection
{
    Inbound,
    Outbound,
}

/// <summary>Connection state of a tracked beacon chain peer, matching the Beacon API's
/// <c>node/peers</c> state set. Not every value is necessarily reachable through every
/// <see cref="IPeerDirectory"/> implementation - see the implementer's own documentation.</summary>
public enum PeerConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
}

/// <summary>
/// A read-only snapshot of what this plugin knows about one peer, shaped for the Beacon API's
/// <c>/eth/v1/node/peers</c> and <c>/eth/v1/node/peers/{peer_id}</c> endpoints.
/// </summary>
/// <param name="PeerId">The libp2p peer id, not the transport address.</param>
/// <param name="LastKnownMultiaddr">The most recently known multiaddr for this peer id.</param>
/// <param name="AgentVersion">The identify protocol's agent/client-version string, when the
/// implementer has it available; <c>null</c> otherwise.</param>
/// <param name="Enr">The peer's discv5 ENR text, when the implementer discovered this peer itself;
/// <c>null</c> for a statically configured peer, an inbound session, or any peer whose ENR the
/// implementer never observed.</param>
public readonly record struct PeerRecord(
    string PeerId,
    PeerDirection Direction,
    PeerConnectionState State,
    string LastKnownMultiaddr,
    string? AgentVersion,
    string? Enr);

/// <summary>Read-only peer directory the Beacon API's <c>node/peers</c> endpoints need. Kept separate
/// from <see cref="IBeaconSyncPeerPool"/> so a range-sync consumer does not have to depend on
/// API-shaped surface it never reads.</summary>
public interface IPeerDirectory
{
    /// <summary>Every peer this implementer currently knows about.</summary>
    IReadOnlyList<PeerRecord> Peers { get; }

    /// <summary>Looks up one peer by its libp2p peer id. Returns <c>false</c> for an empty id or an
    /// id this implementer has no record of - never a record for an unresolved identity.</summary>
    bool TryGetPeer(string peerId, out PeerRecord peer);
}
