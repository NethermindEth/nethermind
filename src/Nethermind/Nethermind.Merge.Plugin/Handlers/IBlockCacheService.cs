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
using Nethermind.Core.Crypto;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IBlockCacheService
{
    public ConcurrentDictionary<Keccak, Block> BlockCache { get; }
    Keccak ProcessDestination { get; set; }
    Keccak SyncingHead { get; set; }
    Keccak FinalizedHash { get; set; }
    
    /*
     * if a block failed somewhere in sync or processing this will be non-null, and `engine_newPayload` will need to
     * return the last valid block
     */
    Block? LastValidBlockBeforeFailure { get; set; }

    /**
     * Run on new block, either in sync or in new payload. Return false if block processing should not continue due to
     * a known invalid ancestor.
     */
    bool PreBlockSuggest(long blockNumber, Keccak parentHash)
    {
        if (LastValidBlockBeforeFailure != null &&
            blockNumber > LastValidBlockBeforeFailure.Number &&
            parentHash != LastValidBlockBeforeFailure.Hash)
        {
            return false;
        }
        
        // Either, everything is going fine, or this block is a new child of the failed block.
        LastValidBlockBeforeFailure = null;
        return true;
    }

    void OnInvalidBlock(Block block)
    {
        LastValidBlockBeforeFailure = block;
    }
}
