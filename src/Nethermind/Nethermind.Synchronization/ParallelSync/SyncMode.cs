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

namespace Nethermind.Synchronization.ParallelSync
{
    [Flags]
    public enum SyncMode
    {
        None = 0,
        
        /// <summary>
        /// We are connected to nodes and processing based on discovery
        /// </summary>
        WaitingForBlock = 1,
        /// <summary>
        /// We are not connected to nodes
        /// </summary>
        Disconnected = 2,
        /// <summary>
        /// Stage of fast sync that downloads headers, bodies or receipts from pivot to beginning of chain in parallel.
        /// </summary>
        FastBlocks = 4,
        /// <summary>
        /// A standard fast sync mode before the peers head - 32 (threshold). It happens after the fast blocks finishes to download from pivot downwards. By default the picot for fast blocks is 0 so the fast blocks finish immediately. 
        /// </summary>
        FastSync = 8,
        /// <summary>
        /// This is the stage of the fast sync when all the trie nodes are downloaded. The node can keep switching between StateNodes and FastSync while it has to catch up with the Head - 32 due to peers not returning old trie nodes.
        /// </summary>
        StateNodes = 16,
        /// <summary>
        /// This is either a standard full archive sync from genesis or full sync after StateNodes finish.
        /// </summary>
        Full = 32,
        /// <summary>
        /// Beam sync is not implemented yet.
        /// </summary>
        Beam = 64,
        /// <summary>
        /// Loading previously downloaded blocks from the DB
        /// </summary>
        DbLoad = 128,
        /// <summary>
        /// Stage of fast sync that downloads headers in parallel.
        /// </summary>
        FastHeaders = FastBlocks | 256,
        /// <summary>
        /// Stage of fast sync that downloads headers in parallel.
        /// </summary>
        FastBodies = FastBlocks | 512,
        /// <summary>
        /// Stage of fast sync that downloads headers in parallel.
        /// </summary>
        FastReceipts = FastBlocks | 1024,
        
        All = WaitingForBlock | Disconnected | FastBlocks | FastSync | StateNodes | StateNodes | Full | Beam | DbLoad | FastHeaders | FastBodies | FastReceipts
    }
    
    public static class SyncModeExtensions
    {
        public static bool NotSyncing(this SyncMode syncMode) => syncMode == SyncMode.WaitingForBlock || syncMode == SyncMode.Disconnected;
    }
}
