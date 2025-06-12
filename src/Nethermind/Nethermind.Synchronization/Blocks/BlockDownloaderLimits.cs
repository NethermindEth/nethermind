// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Synchronization.Blocks
{
    public static class PeerInfoExtensions
    {
        public static int MaxBodiesPerRequest(this PeerInfo peer)
        {
            return peer.PeerClientType switch
            {
                NodeClientType.Besu => BeSuSyncLimits.MaxBodyFetch,
                NodeClientType.Geth => GethSyncLimits.MaxBodyFetch,
                NodeClientType.Nethermind => NethermindSyncLimits.MaxBodyFetch,
                NodeClientType.Parity => ParitySyncLimits.MaxBodyFetch,
                NodeClientType.OpenEthereum => ParitySyncLimits.MaxBodyFetch,
                NodeClientType.Trinity => GethSyncLimits.MaxBodyFetch,
                NodeClientType.Erigon => GethSyncLimits.MaxBodyFetch,
                NodeClientType.Reth => GethSyncLimits.MaxBodyFetch,
                NodeClientType.Unknown => 32,
                _ => throw new ArgumentOutOfRangeException(nameof(peer.PeerClientType), peer.PeerClientType, null)
            };
        }

        public static int MaxReceiptsPerRequest(this PeerInfo peer)
        {
            return peer.PeerClientType switch
            {
                NodeClientType.Besu => BeSuSyncLimits.MaxReceiptFetch,
                NodeClientType.Geth => GethSyncLimits.MaxReceiptFetch,
                NodeClientType.Nethermind => NethermindSyncLimits.MaxReceiptFetch,
                NodeClientType.Parity => ParitySyncLimits.MaxReceiptFetch,
                NodeClientType.OpenEthereum => ParitySyncLimits.MaxReceiptFetch,
                NodeClientType.Trinity => GethSyncLimits.MaxReceiptFetch,
                NodeClientType.Erigon => GethSyncLimits.MaxReceiptFetch,
                NodeClientType.Reth => GethSyncLimits.MaxReceiptFetch,
                NodeClientType.Unknown => 128,
                _ => throw new ArgumentOutOfRangeException(nameof(peer.PeerClientType), peer.PeerClientType, null)
            };
        }

        public static int MaxHeadersPerRequest(this PeerInfo peer)
        {
            return peer.PeerClientType switch
            {
                NodeClientType.Besu => BeSuSyncLimits.MaxHeaderFetch,
                NodeClientType.Geth => GethSyncLimits.MaxHeaderFetch,
                NodeClientType.Nethermind => NethermindSyncLimits.MaxHeaderFetch,
                NodeClientType.Parity => ParitySyncLimits.MaxHeaderFetch,
                NodeClientType.OpenEthereum => ParitySyncLimits.MaxHeaderFetch,
                NodeClientType.Trinity => GethSyncLimits.MaxHeaderFetch,
                NodeClientType.Erigon => GethSyncLimits.MaxHeaderFetch,
                NodeClientType.Reth => GethSyncLimits.MaxHeaderFetch,
                NodeClientType.Unknown => 192,
                _ => throw new ArgumentOutOfRangeException(nameof(peer.PeerClientType), peer.PeerClientType, null)
            };
        }
    }
}
