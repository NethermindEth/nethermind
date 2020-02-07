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

using System.Collections.Generic;
using System.ComponentModel;
using Nethermind.Stats;

namespace Nethermind.Blockchain.Synchronization
{
    /// <summary>
    /// I believe that this interface should actually make it to the original class (and not stay in test)
    /// Then whenever Borrow is invoked - we can pass the peer selection strategy and it can be very helpful when replacing.
    /// Then it can even have IsUpgradeable field
    /// </summary>
    public interface IPeerSelectionStrategy
    {
        string Name { get; }
        bool CanBeReplaced { get; }
        PeerInfo Select(PeerInfo currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree);

        public void CheckAsyncState(PeerInfo info)
        {
            if (info.HasBeenDisconnected)
            {
                throw new InvalidAsynchronousStateException($"{Name} strategy found a disconnected peer - {info}");
            }

            if (info.IsAllocated)
            {
                throw new InvalidAsynchronousStateException($"{Name} strategy found an allocated peer - {info}");
            }

            if (!info.IsInitialized)
            {
                throw new InvalidAsynchronousStateException($"{Name} strategy found an initilized peer - {info}");
            }
        }
    }
}