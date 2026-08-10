// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Init.FlatHistory;

/// <summary>
/// The production <see cref="IImportPeerSink"/> the placeholder's own doc comment calls for: bans route through
/// the same <see cref="ISyncPeerPool.ReportBreachOfProtocol"/> path every other sync feed uses for a misbehaving
/// peer (reputation event + disconnect), and alternate selection is <see cref="NHistPeerSelector"/> re-run
/// excluding every peer banned through this sink so far, plus the specific source passed to
/// <see cref="TryGetAlternateSource"/> even if <see cref="BanSource"/> was not called on it first - callers do not
/// agree on which order to call the two in (compare <see cref="PeerFedWindowImporter"/> vs
/// <see cref="ArchiveCloneImporter"/>).
/// </summary>
public sealed class NHistImportPeerSink(ISyncPeerPool peerPool, NHistPeerSelector selector) : IImportPeerSink
{
    private readonly Lock _lock = new();
    private readonly HashSet<PublicKey> _banned = [];

    public void BanSource(IWindowImportSource source, string reason)
    {
        if (source is not NHistWindowImportSource nhistSource) return;

        lock (_lock) _banned.Add(nhistSource.Peer.SyncPeer.Node.Id);
        peerPool.ReportBreachOfProtocol(nhistSource.Peer, DisconnectReason.BreachOfProtocol, reason);
    }

    public bool TryGetAlternateSource(IWindowImportSource banned, [NotNullWhen(true)] out IWindowImportSource? alternate)
    {
        HashSet<PublicKey> excluded;
        lock (_lock) excluded = [.. _banned];
        if (banned is NHistWindowImportSource nhistBanned) excluded.Add(nhistBanned.Peer.SyncPeer.Node.Id);

        if (selector.TryGetEligibleImportPeer(excluded, out PeerInfo peer, out INHistSyncPeer syncPeer))
        {
            alternate = new NHistWindowImportSource(peer, syncPeer);
            return true;
        }

        alternate = null;
        return false;
    }
}
