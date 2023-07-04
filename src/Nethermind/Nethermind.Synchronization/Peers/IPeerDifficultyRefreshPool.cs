// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;

namespace Nethermind.Synchronization.Peers;

public interface IPeerDifficultyRefreshPool
{
    /// <summary>
    /// All peers maintained by the pool
    /// </summary>
    IEnumerable<PeerInfo> AllPeers { get; }

    void SignalPeersChanged();

    void UpdateSyncPeerHeadIfHeaderIsBetter(ISyncPeer syncPeer, BlockHeader header);

    void ReportRefreshFailed(ISyncPeer syncPeer, string reason, Exception? exception = null);
}
