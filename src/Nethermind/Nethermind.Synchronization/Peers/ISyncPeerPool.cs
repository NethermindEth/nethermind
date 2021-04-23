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
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Peers
{
    public interface ISyncPeerPool : IDisposable
    {
        Task<SyncPeerAllocation> Allocate(IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts allocationContexts, int timeoutMilliseconds = 0);

        void Free(SyncPeerAllocation syncPeerAllocation);

        void ReportNoSyncProgress(PeerInfo peerInfo, AllocationContexts allocationContexts);

        void ReportBreachOfProtocol(PeerInfo peerInfo, string details);
        
        void ReportWeakPeer(PeerInfo peerInfo, AllocationContexts allocationContexts);

        /// <summary>
        /// Wakes up all the sleeping peers.
        /// </summary>
        void WakeUpAll();

        /// <summary>
        /// All peers maintained by the pool
        /// </summary>
        IEnumerable<PeerInfo> AllPeers { get; }

        /// <summary>
        /// All the useful peers available for allocation.
        /// </summary>
        IEnumerable<PeerInfo> InitializedPeers { get; }

        /// <summary>
        /// Number of all sync peers
        /// </summary>
        int PeerCount { get; }

        /// <summary>
        /// Number of peers that are not sleeping
        /// </summary>
        int InitializedPeersCount { get; }

        /// <summary>
        /// Max number of peers
        /// </summary>
        int PeerMaxCount { get; }

        /// <summary>
        /// Invoked when a new connection is established and ETH subprotocol handshake is finished - this node is ready to sync.
        /// </summary>
        /// <param name="syncPeer"></param>
        void AddPeer(ISyncPeer syncPeer);
        
        /// <summary>
        /// Invoked after a session / connection is closed.
        /// </summary>
        /// <param name="syncPeer"></param>
        void RemovePeer(ISyncPeer syncPeer);
        
        /// <summary>
        /// It is hard to track total difficulty so occasionally we send a total difficulty request to update node information.
        /// Specifically when nodes send HintBlock message they do not attach total difficulty information.
        /// </summary>
        /// <param name="syncPeer"></param>
        /// <param name="hash">Hash of a block that we know might be the head block of the peer</param>
        void RefreshTotalDifficulty(ISyncPeer syncPeer, Keccak hash);

        /// <summary>
        /// Starts the pool loops.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops the pool loops
        /// </summary>
        /// <returns></returns>
        Task StopAsync();
        
        PeerInfo? GetPeer(Node node);

        event EventHandler<PeerBlockNotificationEventArgs> NotifyPeerBlock;
    }
}
