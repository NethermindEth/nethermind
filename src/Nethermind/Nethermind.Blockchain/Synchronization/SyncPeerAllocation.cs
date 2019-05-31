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

namespace Nethermind.Blockchain.Synchronization
{   
    public class SyncPeerAllocation
    {
        private static object _allocationLock = new object(); // why is it static? better do not touch
        
        public long? MinBlocksAhead { get; set; }
        public bool CanBeReplaced { get; set; } = true;
        public string Description { get; }
        public PeerInfo Current { get; private set; }

        public SyncPeerAllocation(PeerInfo initialPeer, string description)
        {
            lock (_allocationLock)
            {
                if (!initialPeer.IsAllocated)
                {
                    initialPeer.IsAllocated = true;
                    Current = initialPeer;
                }
            }

            Description = description;
        }

        public SyncPeerAllocation(string description)
        {
            Description = description;
        }

        public void ReplaceCurrent(PeerInfo betterPeer)
        {
            if (betterPeer == null)
            {
                throw new ArgumentNullException(nameof(betterPeer));
            }

            AllocationChangeEventArgs args;
            lock (_allocationLock)
            {
                PeerInfo current = Current;
                if (current != null && !CanBeReplaced)
                {
                    return;
                }
                
                if (betterPeer.IsAllocated)
                {
                    return;
                }

                betterPeer.IsAllocated = true;
                if (current != null)
                {
                    current.IsAllocated = false;
                }

                args = new AllocationChangeEventArgs(current, betterPeer);
                Current = betterPeer;
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

        public void FinishSync()
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
        }
    }
}