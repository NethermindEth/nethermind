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
        
        public UInt256 TotalDifficulty { get; set; }
        
        public ISyncPeer Current { get; set; }

        public void DisconnectCurrent()
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
        
        public void ReplaceCurrent(ISyncPeer betterPeer)
        {
            Replaced?.Invoke(this, new SyncPeerEventArgs(betterPeer));
        }
        
        public void Cancel()
        {
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler<SyncPeerEventArgs> Replaced;

        public event EventHandler Disconnected;
        
        public event EventHandler Cancelled;

        public override string ToString()
        {
            return string.Concat("[Allocation|", Description, "]");
        }
    }
}