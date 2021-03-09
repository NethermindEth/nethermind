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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Db.Blooms
{
    public class NullBloomStorage : IBloomStorage
    {
        private NullBloomStorage()
        {
        }
        
        public static NullBloomStorage Instance { get; } = new();
        public long MinBlockNumber { get; } = long.MaxValue;
        public long MaxBlockNumber { get; } = 0;
        public long MigratedBlockNumber { get; } = -1;

        public void Store(long blockNumber, Core.Bloom bloom) { }
        public void Migrate(IEnumerable<BlockHeader> blockHeaders) { }

        public IBloomEnumeration GetBlooms(long fromBlock, long toBlock) => new NullBloomEnumerator();

        public bool ContainsRange(in long fromBlockNumber, in long toBlockNumber) => false;

        public IEnumerable<Average> Averages { get; } = Array.Empty<Average>();
        

        private class NullBloomEnumerator : IBloomEnumeration
        {
            public IEnumerator<Core.Bloom> GetEnumerator() => Enumerable.Empty<Core.Bloom>().GetEnumerator();
            
            public bool TryGetBlockNumber(out long blockNumber)
            {
                blockNumber = default;
                return false;
            }

            public (long FromBlock, long ToBlock) CurrentIndices { get; } = (0, 0);

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        public void Dispose() { }
    }
}
