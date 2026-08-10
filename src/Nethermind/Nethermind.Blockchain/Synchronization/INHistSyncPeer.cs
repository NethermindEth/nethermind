// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.State;

namespace Nethermind.Blockchain.Synchronization
{
    /// <summary>One page of changeset chunks, as returned by <see cref="INHistSyncPeer.GetChangesets"/>. An empty
    /// <see cref="Chunks"/> means the peer had nothing to serve for the requested range (either it is genuinely
    /// exhausted, or the peer refused) - the caller distinguishes those by re-requesting from the same
    /// <c>fromBlockInclusive</c> and treating a second empty page as exhaustion, matching the wire's own
    /// no-cursor, range-bounded contract for <c>GetChangesets</c>.</summary>
    public readonly record struct NHistChangesetsPage(IReadOnlyList<ChangesetChunkEntry> Chunks);

    /// <summary>One page of history rows, as returned by <see cref="INHistSyncPeer.GetHistoryRows"/> - the same
    /// shape <see cref="Nethermind.State.Flat.History.ArchiveCloneRowPage"/> in the state-flat-history layer needs,
    /// kept as an independent type here so this project never has to reference that layer just to describe a
    /// devp2p response.</summary>
    public readonly record struct NHistRowsPage(IReadOnlyList<HistoryRowEntry> Entries, byte[]? NextCursor, bool Refused);

    /// <summary>
    /// The nhist1 satellite protocol surface a sync peer exposes to consumers outside <c>Nethermind.Network</c> -
    /// the same seam <see cref="ISnapSyncPeer"/> is for snap. Implemented directly by the protocol handler; reached
    /// via <see cref="IPeerWithSatelliteProtocol.TryGetSatelliteProtocol{T}"/> on <see cref="ISyncPeer"/>, exactly
    /// like snap. Return types are the plain domain records from <c>Nethermind.State</c>
    /// (<see cref="ChangesetChunkEntry"/>/<see cref="HistoryRowEntry"/>), never the pooled wire message types -
    /// those are owned and disposed entirely inside the protocol handler.
    /// </summary>
    public interface INHistSyncPeer
    {
        HistoryServingScope[] PeerServedScopes { get; }

        bool PeerSupportsFullClone { get; }

        byte PeerRowFormatVersion { get; }

        Task<NHistChangesetsPage> GetChangesets(ulong fromBlockInclusive, ulong toBlockInclusive, CancellationToken token);

        Task<NHistRowsPage> GetHistoryRows(HistoryRowColumn column, byte[] startKey, byte[] endKey, byte[]? cursor, CancellationToken token);
    }
}
