// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.State;
using Nethermind.State.Flat.History;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Init.FlatHistory;

/// <summary>
/// devp2p-backed <see cref="IArchiveCloneSource"/>: a thin wrapper over one peer's
/// <see cref="INHistSyncPeer.GetHistoryRows"/> client method. Unlike <see cref="NHistWindowImportSource"/>, no
/// internal pagination loop is needed here - <c>GetHistoryRowsMessage</c> already carries a cursor, and
/// <see cref="ArchiveCloneImporter"/> drives the page-by-page loop itself, so this type only needs to translate
/// one call to one call and preserve the <see cref="ArchiveCloneRowPage.Refused"/> signal end to end.
/// </summary>
public sealed class NHistArchiveCloneSource(PeerInfo peer, INHistSyncPeer syncPeer, byte rowFormatVersion, ulong watermark) : IArchiveCloneSource
{
    /// <summary>A full-clone-capable node advertises its coverage as the single all-keys scope
    /// <see cref="Nethermind.State.Flat.History.HistoryServer.ServedScopes"/> publishes for an unwindowed database
    /// - there is exactly one entry in the ordinary case this is meant to support, so its watermark is taken as
    /// the clone's watermark; a peer with zero scopes has nothing usable yet.</summary>
    public static NHistArchiveCloneSource FromPeer(PeerInfo peer, INHistSyncPeer syncPeer)
        => new(peer, syncPeer, syncPeer.PeerRowFormatVersion, WatermarkOf(syncPeer));

    public static ulong WatermarkOf(INHistSyncPeer syncPeer)
        => syncPeer.PeerServedScopes.Length > 0 ? syncPeer.PeerServedScopes[0].WatermarkBlock : 0;

    public PeerInfo Peer => peer;

    public bool SupportsFullClone => syncPeer.PeerSupportsFullClone;

    public byte RowFormatVersion => rowFormatVersion;

    public ulong Watermark => watermark;

    public async Task<ArchiveCloneRowPage> GetHistoryRowsAsync(
        HistoryRowColumn column, byte[] startKey, byte[] endKey, byte[]? cursor, CancellationToken cancellationToken)
    {
        NHistRowsPage page = await syncPeer.GetHistoryRows(column, startKey, endKey, cursor, cancellationToken);
        return new ArchiveCloneRowPage(page.Entries, page.NextCursor, page.Refused);
    }
}
