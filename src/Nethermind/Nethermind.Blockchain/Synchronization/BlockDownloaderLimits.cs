//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Blockchain.Synchronization.SyncLimits;

namespace Nethermind.Blockchain.Synchronization
{
    public static class PeerInfoExtensions
    {
        public static int MaxBodiesPerRequest(this PeerInfo peer)
        {
            return peer.PeerClientType switch
            {
                PeerClientType.BeSu => BeSuSyncLimits.MaxBodyFetch,
                PeerClientType.Geth => GethSyncLimits.MaxBodyFetch,
                PeerClientType.Nethermind => NethermindSyncLimits.MaxBodyFetch,
                PeerClientType.Parity => ParitySyncLimits.MaxBodyFetch,
                PeerClientType.Unknown => 32,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        public static int MaxReceiptsPerRequest(this PeerInfo peer)
        {
            return peer.PeerClientType switch
            {
                PeerClientType.BeSu => BeSuSyncLimits.MaxReceiptFetch,
                PeerClientType.Geth => GethSyncLimits.MaxReceiptFetch,
                PeerClientType.Nethermind => NethermindSyncLimits.MaxReceiptFetch,
                PeerClientType.Parity => ParitySyncLimits.MaxReceiptFetch,
                PeerClientType.Unknown => 128,
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        
        public static int MaxHeadersPerRequest(this PeerInfo peer)
        {
            return peer.PeerClientType switch
            {
                PeerClientType.BeSu => BeSuSyncLimits.MaxHeaderFetch,
                PeerClientType.Geth => GethSyncLimits.MaxHeaderFetch,
                PeerClientType.Nethermind => NethermindSyncLimits.MaxHeaderFetch,
                PeerClientType.Parity => ParitySyncLimits.MaxHeaderFetch,
                PeerClientType.Unknown => 192,
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }
}