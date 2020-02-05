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

namespace Nethermind.Blockchain.Synchronization
{
    [Flags]
    public enum PeerSelectionOptions
    {
        None = 0,
        
        /// <summary>
        /// Try to allocate a peer with high latency / low quality first
        /// </summary>
        LowPriority = 1,
        
        /// <summary>
        /// Require only peers with higher total difficulty to be allocated
        /// </summary>
        HigherTotalDiff = 2,
        
        /// <summary>
        /// Do not try to upgrade this peer allocation before it is freed or cancelled.
        /// </summary>
        DoNotReplace = 4,
        All = 7,
    }
}