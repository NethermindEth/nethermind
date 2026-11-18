// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.Peers;

public interface IPeerDifficultyRefreshPool
{
    /// <summary>
    /// All peers maintained by the pool
    /// </summary>
    IEnumerable<PeerInfo> AllPeers { get; }

    void SignalPeersChanged();

    /// <summary>Applies a header supported by local storage or an original consensus request.</summary>
    /// <remarks>The requested hash must come from local consensus, never from the response; zero means no consensus request.</remarks>
    void UpdateSyncPeerHeadIfHeaderIsBetter(ISyncPeer syncPeer, BlockHeader header, Hash256 consensusRequestedHash);

    void ReportRefreshFailed(ISyncPeer syncPeer, string reason, Exception? exception = null);
}
