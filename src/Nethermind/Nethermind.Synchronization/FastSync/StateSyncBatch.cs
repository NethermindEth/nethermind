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
using System.Diagnostics;
using System.Threading;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Synchronization.FastSync
{
    [DebuggerDisplay("Requested Nodes: {RequestedNodes?.Length ?? 0}, Responses: {Responses?.Length ?? 0}, Assigned: {AssignedPeer?.Current}")]
    public class StateSyncBatch
    {
        private static int BatchNumber = 0;
        
        private int MyBatchNumber = 0;

        public StateSyncBatch()
        {
            MyBatchNumber = Interlocked.Increment(ref BatchNumber);
        }
        
        public int Type { get; set; }
        
        public static StateSyncBatch Empty = new StateSyncBatch{RequestedNodes = Array.Empty<StateSyncItem>()};
        
        public StateSyncItem[] RequestedNodes { get; set; }
        
        public byte[][] Responses { get; set; }

        public bool IsAdditionalDataConsumer { get; set; }
        
        public UInt256 RequiredPeerDifficulty { get; set; }

        public int ConsumerId { get; set; }

        public override string ToString()
        {
            return $"state sync batch {MyBatchNumber}";
        }
    }
}