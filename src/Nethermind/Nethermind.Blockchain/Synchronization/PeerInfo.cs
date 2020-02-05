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
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Blockchain.Synchronization
{
    public enum PeerClientType
    {
        BeSu,
        Geth,
        Nethermind,
        Parity,
        Unknown
    }

    public class PeerInfo
    {
        public PeerInfo(ISyncPeer syncPeer)
        {
            SyncPeer = syncPeer;
            TotalDifficulty = syncPeer.TotalDifficultyOnSessionStart;

            if (syncPeer.ClientId == null)
            {
                Console.WriteLine("CLIENT ID IS NULL WHEN PEER INFO IS CREATED");
            }

            if (syncPeer.ClientId?.Contains("BeSu", StringComparison.InvariantCultureIgnoreCase) ?? false)
            {
                PeerClientType = PeerClientType.BeSu;
            }
            else if (syncPeer.ClientId?.Contains("Geth", StringComparison.InvariantCultureIgnoreCase) ?? false)
            {
                PeerClientType = PeerClientType.Geth;
            }
            else if (syncPeer.ClientId?.Contains("Nethermind", StringComparison.InvariantCultureIgnoreCase) ?? false)
            {
                PeerClientType = PeerClientType.Nethermind;
            }
            else if (syncPeer.ClientId?.Contains("Parity", StringComparison.InvariantCultureIgnoreCase) ?? false)
            {
                PeerClientType = PeerClientType.Parity;
            }
            else
            {
                PeerClientType = PeerClientType.Unknown;
            }
        }

        public PeerClientType PeerClientType { get; }
        public bool IsAllocated { get; set; }
        public bool IsInitialized { get; set; }
        public DateTime? SleepingSince { get; set; }

        public bool IsSleepingDeeply { get; set; }
        public bool IsAsleep => SleepingSince != null;
        public ISyncPeer SyncPeer { get; }
        public UInt256 TotalDifficulty { get; set; }
        public long HeadNumber { get; set; }
        public Keccak HeadHash { get; set; }

        public override string ToString() => $"[Peer|{SyncPeer?.Node:s}|{HeadNumber}|{SyncPeer?.ClientId}]";

        public string ToString(string format) => ToString(format, null);

        public string ToString(string format, IFormatProvider formatProvider) => $"[Peer|{SyncPeer?.Node.ToString(format)}|{HeadNumber}|{SyncPeer?.ClientId}]";
    }
}