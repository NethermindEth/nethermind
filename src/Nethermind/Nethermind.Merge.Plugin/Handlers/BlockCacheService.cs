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

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

public class BlockCacheService : IBlockCacheService
{
    public ConcurrentDictionary<Keccak, Block> BlockCache { get; } = new();
    public Keccak? ProcessDestination { get; set; }
    public Keccak? SyncingHead { get; set; }
    public Keccak FinalizedHash { get; set; } = Keccak.Zero;

    // Sometimes the full block is not available. 
    LruCache<Keccak, Keccak> _hashTree = new LruCache<Keccak, Keccak>(256, "HashTree");
    LruCache<Keccak, Keccak> _failedBlock = new LruCache<Keccak, Keccak>(256, "FailedBlock");
    
    public void SuggestChildParent(Keccak child, Keccak parent)
    {
        _hashTree.Set(child, parent);
    }

    public void OnInvalidBlock(Keccak failedBlock, Keccak parent)
    {
        _failedBlock.Set(failedBlock, parent);
    }

    public bool IsOnKnownInvalidChain(Keccak blockHash, out Keccak? lastValidHash, int lookupLimit = 16)
    {
        if (_failedBlock.TryGet(blockHash, out lastValidHash))
        {
            return true;
        }

        if (lookupLimit != 0 && _hashTree.TryGet(blockHash, out Keccak parentBlock))
        {
            // TODO: Add a definitely valid block check, so it would stop early
            if(IsOnKnownInvalidChain(blockHash, out lastValidHash, lookupLimit - 1))
            {
                // So that further call is O(1)
                _failedBlock.Set(blockHash, lastValidHash);
                return true;
            }
        }
        
        return false;
    }

    public Block? LastValidBlockBeforeFailure { get; set; } = null;
}
