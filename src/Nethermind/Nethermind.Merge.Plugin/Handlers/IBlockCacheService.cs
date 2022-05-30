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
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IBlockCacheService
{
    public ConcurrentDictionary<Keccak, Block> BlockCache { get; }
    Keccak ProcessDestination { get; set; }
    Keccak SyncingHead { get; set; }
    Keccak FinalizedHash { get; set; }

    /// <summary>
    /// Suggest that these hash are child parent of each other. Used to determine if a hash is on an invalid chain
    /// </summary>
    /// <param name="child"></param>
    /// <param name="parent"></param>
    void SuggestChildParent(Keccak child, Keccak parent);

    /// <summary>
    /// Mark the block hash as a failed block.
    /// </summary>
    /// <param name="failedBlock"></param>
    /// <param name="parent"></param>
    void OnInvalidBlock(Keccak failedBlock, Keccak parent);
    
    /// <summary>
    /// Return last valid hash if this block is known to be on an invalid chain.
    /// Return null otherwise
    /// </summary>
    bool IsOnKnownInvalidChain(Keccak blockHash, out Keccak? lastValidHash, int lookupLimit = 16);
}
