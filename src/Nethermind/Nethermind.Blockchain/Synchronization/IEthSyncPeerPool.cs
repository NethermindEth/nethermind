/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Synchronization
{
    public interface IEthSyncPeerPool
    {
        bool TryFind(PublicKey nodeId, out PeerInfo peerInfo);
        
        SyncPeerAllocation Borrow(BorrowOptions borrowOptions, string description = "", long? minNumber = null);
        
        SyncPeerAllocation Borrow(string description = "");
        
        void Free(SyncPeerAllocation syncPeerAllocation);
        
        void ReportNoSyncProgress(SyncPeerAllocation syncPeerAllocation);
        
        void ReportNoSyncProgress(PeerInfo peerInfo);
        
        void ReportInvalid(SyncPeerAllocation allocation);
        
        void ReportInvalid(PeerInfo peerInfo);
        
        IEnumerable<PeerInfo> AllPeers { get; }
        
        IEnumerable<PeerInfo> UsefulPeers { get; }
        
        IEnumerable<SyncPeerAllocation> Allocations { get; }
        
        int PeerCount { get; }
        
        int UsefulPeerCount { get; }
        
        int PeerMaxCount { get; }
        
        void Refresh(PublicKey nodeId);
        
        void RemovePeer(ISyncPeer syncPeer);
        
        void AddPeer(ISyncPeer syncPeer);
        
        void Start();
        
        Task StopAsync();
        
        void EnsureBest();
        
        void ReportBadPeer(SyncPeerAllocation batchAssignedPeer);
    }
}