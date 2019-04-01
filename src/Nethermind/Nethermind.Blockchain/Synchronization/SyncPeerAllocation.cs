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
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Synchronization
{
    public class SyncPeerAllocation
    {
        public string Description { get; set; }
        
        public PeerInfo Current { get; private set; }

        public SyncPeerAllocation(PeerInfo initialPeer)
        {
            Current = initialPeer;
        }

        public void ReplaceCurrent(PeerInfo betterPeer)
        {
            AllocationChangeEventArgs args = new AllocationChangeEventArgs(Current, betterPeer);
            Current = betterPeer;
            Replaced?.Invoke(this, args);
         
        }
        
        public void Cancel()
        {
            AllocationChangeEventArgs args = new AllocationChangeEventArgs(Current, null);
            Current = null;
            Cancelled?.Invoke(this, args);
        }

        public event EventHandler<AllocationChangeEventArgs> Replaced;

        public event EventHandler<AllocationChangeEventArgs> Cancelled;

        public override string ToString()
        {
            return string.Concat("[Allocation|", Description, "]");
        }

        public void FinishSync()
        {
            Current = null;
        }
    }
}