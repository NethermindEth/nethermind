// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Synchronization.Blocks
{
    public static class PeerInfoExtensions
    {
        /// <summary>
        /// The number of headers <paramref name="peer"/> is expected to serve in a single request.
        /// </summary>
        /// <remarks>
        /// Client types with no known limit fall back to the Geth one, which every client serves. Throwing
        /// instead would abort the sync loop whenever a peer runs a client that was added to
        /// <see cref="NodeClientType"/> but not listed here.
        /// </remarks>
        public static int MaxHeadersPerRequest(this PeerInfo peer) => peer.PeerClientType switch
        {
            NodeClientType.Besu => BeSuSyncLimits.MaxHeaderFetch,
            NodeClientType.Geth => GethSyncLimits.MaxHeaderFetch,
            NodeClientType.Nethermind => NethermindSyncLimits.MaxHeaderFetch,
            NodeClientType.Parity => ParitySyncLimits.MaxHeaderFetch,
            NodeClientType.OpenEthereum => ParitySyncLimits.MaxHeaderFetch,
            NodeClientType.Trinity => GethSyncLimits.MaxHeaderFetch,
            NodeClientType.Erigon => GethSyncLimits.MaxHeaderFetch,
            NodeClientType.Reth => GethSyncLimits.MaxHeaderFetch,
            _ => GethSyncLimits.MaxHeaderFetch
        };
    }
}
