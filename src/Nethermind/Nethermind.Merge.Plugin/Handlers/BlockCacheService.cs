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
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

public class BlockCacheService : IBlockCacheService
{
    private readonly ConcurrentDictionary<Keccak, BlockHeader> _blockHeadersCache = new();
    private readonly ConcurrentQueue<BlockHeader> _blockHeadersQueue = new();

    public int Count => _blockHeadersQueue.Count;
    
    public bool Contains(Keccak blockHash)
    {
        return _blockHeadersCache.ContainsKey(blockHash);
    }
    
    public BlockHeader? GetBlockHeader(Keccak blockHash)
    {
        _blockHeadersCache.TryGetValue(blockHash, out BlockHeader? blockHeader);
        return blockHeader;
    }
    
    public IEnumerable<BlockHeader> GetBlockHeadersUpToNumber(long blockNumber)
    {
        return _blockHeadersCache.Values.Where(h => h.Number < blockNumber);
    }
    
    public bool EnqueueBlockHeader(BlockHeader blockHeader)
    {
        if (blockHeader.Hash is null)
        {
            return false;
        }
        _blockHeadersQueue.Enqueue(blockHeader);
        return _blockHeadersCache.TryAdd(blockHeader.Hash, blockHeader);
    }

    public BlockHeader? DequeueBlockHeader()
    {
        _blockHeadersQueue.TryDequeue(out BlockHeader? blockHeader);
        if (blockHeader != null)
        {
            _blockHeadersCache.TryRemove(blockHeader.Hash, out blockHeader);
            return blockHeader;
        }

        return null;
    }
    
    public void RemoveBlockHeadersUpToNumber(long blockNumber)
    {
        IEnumerable<Keccak> hashes = _blockHeadersCache
            .Where(x => x.Value.Number <= blockNumber)
            .Select(x => x.Key);
        foreach (Keccak hash in hashes)
        {
            _blockHeadersCache.TryRemove(hash, out _);
        }
    }
}
