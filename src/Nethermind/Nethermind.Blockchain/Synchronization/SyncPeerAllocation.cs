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
using System.ComponentModel;
using Nethermind.Stats;

namespace Nethermind.Blockchain.Synchronization
{
    public class SyncPeerAllocation
    {
        public static SyncPeerAllocation FailedAllocation = new SyncPeerAllocation(NullStrategy.Instance);

        /// <summary>
        /// this should be used whenever we change IsAllocated property on PeerInfo-
        /// </summary>
        private static object _allocationLock = new object();

        private IPeerSelectionStrategy _peerSelectionStrategy;
        private string Description => _peerSelectionStrategy.Name;
        public PeerInfo Current { get; private set; }

        public bool HasPeer => Current != null;

        public SyncPeerAllocation(PeerInfo peerInfo)
            : this(new StaticSelectionStrategy(peerInfo))
        {
        }

        public SyncPeerAllocation(IPeerSelectionStrategy peerSelectionStrategy)
        {
            _peerSelectionStrategy = peerSelectionStrategy;
        }

        public void AllocateBestPeer(IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            PeerInfo current = Current;
            PeerInfo selected = _peerSelectionStrategy.Select(Current, peers, nodeStatsManager, blockTree);
            if (selected == current)
            {
                return;
            }

            AllocationChangeEventArgs args;
            lock (_allocationLock)
            {
                if (selected.IsAllocated)
                {
                    throw new InvalidAsynchronousStateException("Selected an already allocated peer");
                }

                selected.IsAllocated = true;
                Current = selected;
                args = new AllocationChangeEventArgs(current, selected);
                if (current != null)
                {
                    current.IsAllocated = false;
                }
            }

            Replaced?.Invoke(this, args);
        }

        public void Refresh()
        {
            if (Current != null)
            {
                Refreshed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Cancel()
        {
            PeerInfo current = Current;
            if (current == null)
            {
                return;
            }

            lock (_allocationLock)
            {
                current.IsAllocated = false;
                Current = null;
            }

            AllocationChangeEventArgs args = new AllocationChangeEventArgs(current, null);
            Cancelled?.Invoke(this, args);
        }

        public event EventHandler<AllocationChangeEventArgs> Replaced;

        public event EventHandler<AllocationChangeEventArgs> Cancelled;

        public event EventHandler Refreshed;

        public override string ToString()
        {
            return string.Concat("[Allocation|", Description, "]");
        }
    }
}