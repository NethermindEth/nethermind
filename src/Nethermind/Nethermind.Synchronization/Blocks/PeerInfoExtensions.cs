// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Synchronization.Blocks
{
    public static class PeerInfoExtensions
    {
        public static int MaxHeadersPerRequest(this PeerInfo peer) => peer.PeerClientType switch
        {
            NodeClientType.Besu => BeSuSyncLimits.MaxHeaderFetch,
            NodeClientType.Geth => GethSyncLimits.MaxHeaderFetch,
            NodeClientType.Nethermind => NethermindSyncLimits.MaxHeaderFetch,
            NodeClientType.Erigon => GethSyncLimits.MaxHeaderFetch,
            NodeClientType.Reth => GethSyncLimits.MaxHeaderFetch,
            _ => GethSyncLimits.MaxHeaderFetch
        };
    }
}
