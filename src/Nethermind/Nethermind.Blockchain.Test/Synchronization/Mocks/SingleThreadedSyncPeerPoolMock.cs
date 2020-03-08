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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Stats;

namespace Nethermind.Blockchain.Test.Synchronization.Mocks
{
    public class SingleThreadedSyncPeerPoolMock : IEthSyncPeerPool
    {
        public IPeerSelectionStrategy SelectionStrategy { get; set; } = new FirstFree();
        
        public bool TryFind(PublicKey nodeId, out PeerInfo peerInfo)
        {
            throw new NotImplementedException();
        }

        public IBlockTree SyncPeerTree { get; set; } = Build.A.BlockTree().OfChainLength(1).TestObject;

        public Task<SyncPeerAllocation> BorrowAsync(IPeerSelectionStrategy peerSelectionStrategy, string description = "", int timeoutMilliseconds = 0)
        {
            SyncPeerAllocation allocation = new SyncPeerAllocation(peerSelectionStrategy);
            allocation.AllocateBestPeer(UsefulPeers.Where(p => !p.IsAllocated), new NodeStatsManager(new StatsConfig(), LimboLogs.Instance), SyncPeerTree);
            return Task.FromResult(allocation);
        }

        public void Free(SyncPeerAllocation syncPeerAllocation)
        {
            syncPeerAllocation.Cancel();
        }
        
        public void ReportNoSyncProgress(PeerInfo peerInfo, bool isSevere = true)
        {
            NoSyncProgressReports.Enqueue(peerInfo);
            RemovePeer(peerInfo.SyncPeer);
        }

        public void ReportInvalid(PeerInfo peerInfo, string details)
        {
            InvalidPeerReports.Enqueue(peerInfo);
            RemovePeer(peerInfo.SyncPeer);
        }

        public IEnumerable<PeerInfo> AllPeers { get; } = new List<PeerInfo>();
        public IEnumerable<PeerInfo> UsefulPeers => AllPeers.Where(p => !p.IsAsleep);
        public int PeerCount => AllPeers.Count();
        public int UsefulPeerCount => UsefulPeers.Count();
        public int PeerMaxCount { get; } = 25;
        
        public void RefreshTotalDifficulty(PeerInfo peerInfo, Keccak hash)
        {
            // can try to mock delays 
        }

        public void RemovePeer(ISyncPeer syncPeer)
        {
            var peers = AllPeers as List<PeerInfo>;
            var peer = peers.SingleOrDefault(p => p.SyncPeer == syncPeer);
            peers.Remove(peer);
        }

        public void AddPeer(ISyncPeer syncPeer)
        {
            var peers = AllPeers as List<PeerInfo>;
            peers.Add(new PeerInfo(syncPeer));
            PeerAdded?.Invoke(this, EventArgs.Empty);
        }

        public void Start()
        {
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }

        public void ReportWeakPeer(SyncPeerAllocation allocation)
        {
            WeakPeerReports.Enqueue(allocation.Current);
        }

        public Queue<PeerInfo> WeakPeerReports { get; set; } = new Queue<PeerInfo>();
        public Queue<PeerInfo> InvalidPeerReports { get; set; } = new Queue<PeerInfo>();
        public Queue<PeerInfo> NoSyncProgressReports { get; set; } = new Queue<PeerInfo>();

        public void WakeUpAll()
        {
            foreach (PeerInfo peerInfo in AllPeers)
            {
                peerInfo.SleepingSince = null;
            }
        }

        public event EventHandler PeerAdded;

        public void Dispose()
        {
        }
    }
}