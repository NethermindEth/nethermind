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

/// <summary>The <see cref="IArchiveClonePeerSink"/> counterpart to <see cref="NHistImportPeerSink"/> - same
/// ban/alternate-selection policy, scoped to full-clone-eligible peers only (see
/// <see cref="NHistPeerSelector.TryGetEligibleCloneSource"/>'s row-format check).</summary>
public sealed class NHistArchiveClonePeerSink(ISyncPeerPool peerPool, NHistPeerSelector selector, byte requiredRowFormatVersion) : IArchiveClonePeerSink
{
    private readonly Lock _lock = new();
    private readonly HashSet<PublicKey> _banned = [];

    public void BanSource(IArchiveCloneSource source, string reason)
    {
        if (source is not NHistArchiveCloneSource nhistSource) return;

        lock (_lock) _banned.Add(nhistSource.Peer.SyncPeer.Node.Id);
        peerPool.ReportBreachOfProtocol(nhistSource.Peer, DisconnectReason.BreachOfProtocol, reason);
    }

    public bool TryGetAlternateSource(IArchiveCloneSource banned, [NotNullWhen(true)] out IArchiveCloneSource? alternate)
    {
        HashSet<PublicKey> excluded;
        lock (_lock) excluded = [.. _banned];
        if (banned is NHistArchiveCloneSource nhistBanned) excluded.Add(nhistBanned.Peer.SyncPeer.Node.Id);

        if (selector.TryGetEligibleCloneSource(requiredRowFormatVersion, excluded, out PeerInfo peer, out INHistSyncPeer syncPeer))
        {
            alternate = NHistArchiveCloneSource.FromPeer(peer, syncPeer);
            return true;
        }

        alternate = null;
        return false;
    }
}
