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
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    internal class HashCache
    {
        private readonly LruKeyCache<Keccak> _hashCache = new(MemoryAllowance.TxHashCacheSize,
            Math.Min(1024 * 16, MemoryAllowance.TxHashCacheSize), "tx hashes");
        
        private readonly LruKeyCache<Keccak> _hashCacheThisBlock = new(1024 * 16,
            Math.Min(1024 * 16, MemoryAllowance.TxHashCacheSize), "tx hashes");

        public bool Get(Keccak hash)
        {
            return _hashCacheThisBlock.Get(hash) || _hashCache.Get(hash);
        }
        
        public void Set(Keccak hash)
        {
            _hashCache.Set(hash);
        }
        
        public void SetForThisBlock(Keccak hash)
        {
            _hashCacheThisBlock.Set(hash);
        }
        
        public void Delete(Keccak hash)
        {
            _hashCache.Delete(hash);
            _hashCacheThisBlock.Delete(hash);
        }

        public void ClearThisBlock()
        {
            _hashCacheThisBlock.Clear();
        }
    }
}
