//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SyncLimits;

namespace Nethermind.Synchronization.Blocks
{
    public static class PeerInfoExtensions
    {
        public static int MaxBodiesPerRequest(this PeerInfo peer)
        {
            return peer.PeerClientType switch
            {
                NodeClientType.BeSu => BeSuSyncLimits.MaxBodyFetch,
                NodeClientType.Geth => GethSyncLimits.MaxBodyFetch,
                NodeClientType.Nethermind => NethermindSyncLimits.MaxBodyFetch,
                NodeClientType.Parity => ParitySyncLimits.MaxBodyFetch,
                NodeClientType.OpenEthereum => ParitySyncLimits.MaxBodyFetch,
                NodeClientType.Trinity => GethSyncLimits.MaxBodyFetch,
                NodeClientType.Unknown => 32,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        public static int MaxReceiptsPerRequest(this PeerInfo peer)
        {
            return peer.PeerClientType switch
            {
                NodeClientType.BeSu => BeSuSyncLimits.MaxReceiptFetch,
                NodeClientType.Geth => GethSyncLimits.MaxReceiptFetch,
                NodeClientType.Nethermind => NethermindSyncLimits.MaxReceiptFetch,
                NodeClientType.Parity => ParitySyncLimits.MaxReceiptFetch,
                NodeClientType.OpenEthereum => ParitySyncLimits.MaxReceiptFetch,
                NodeClientType.Unknown => 128,
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        
        public static int MaxHeadersPerRequest(this PeerInfo peer)
        {
            return peer.PeerClientType switch
            {
               NodeClientType.BeSu => BeSuSyncLimits.MaxHeaderFetch,
               NodeClientType.Geth => GethSyncLimits.MaxHeaderFetch,
               NodeClientType.Nethermind => NethermindSyncLimits.MaxHeaderFetch,
               NodeClientType.Parity => ParitySyncLimits.MaxHeaderFetch,
               NodeClientType.OpenEthereum => ParitySyncLimits.MaxHeaderFetch,
               NodeClientType.Unknown => 192,
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }
}
