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
using Nethermind.Blockchain;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Peers
{
    public class SyncPeerAllocation
    {
        public static SyncPeerAllocation FailedAllocation = new(NullStrategy.Instance, AllocationContexts.None);

        /// <summary>
        /// this should be used whenever we change IsAllocated property on PeerInfo-
        /// </summary>
        private static object _allocationLock = new();

        private IPeerAllocationStrategy _peerAllocationStrategy;
        public AllocationContexts Contexts { get; }
        public PeerInfo? Current { get; private set; }

        public bool HasPeer => Current != null;

        public SyncPeerAllocation(PeerInfo peerInfo, AllocationContexts contexts)
            : this(new StaticStrategy(peerInfo), contexts)
        {
        }

        public SyncPeerAllocation(IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts contexts)
        {
            _peerAllocationStrategy = peerAllocationStrategy;
            Contexts = contexts;
        }

        public void AllocateBestPeer(
            IEnumerable<PeerInfo> peers,
            INodeStatsManager nodeStatsManager,
            IBlockTree blockTree)
        {
            PeerInfo? current = Current;
            PeerInfo? selected = _peerAllocationStrategy.Allocate(Current, peers, nodeStatsManager, blockTree);
            if (selected == current)
            {
                return;
            }

            lock (_allocationLock)
            {
                if (selected != null && selected.TryAllocate(Contexts))
                {
                    Current = selected;
                    AllocationChangeEventArgs args = new(current, selected);
                    current?.Free(Contexts);
                    Replaced?.Invoke(this, args);
                }
            }
        }

        public void Cancel()
        {
            PeerInfo? current = Current;
            if (current == null)
            {
                return;
            }

            lock (_allocationLock)
            {
                current.Free(Contexts);
                Current = null;
            }

            AllocationChangeEventArgs args = new(current, null);
            Cancelled?.Invoke(this, args);
        }

        public event EventHandler<AllocationChangeEventArgs>? Replaced;

        public event EventHandler<AllocationChangeEventArgs>? Cancelled;

        public override string ToString()
        {
            return $"[Allocation|{Current}]";
        }
    }
}
