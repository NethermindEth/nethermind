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

using System.Collections;

namespace Nethermind.Blockchain.Bloom
{
    public class NullBloomStorage : IBloomStorage
    {
        public static NullBloomStorage Instance { get; } = new NullBloomStorage();
        
        public long MinBlockNumber => -1;
        public long MaxBlockNumber => -1;
        public void Store(long blockNumber, Core.Bloom bloom) { }

        public IBloomEnumerator GetBlooms(long fromBlock, long toBlock)
        {
            return new NullBloomEnumerator();
        }

        private class NullBloomEnumerator : IBloomEnumerator
        {
            public bool MoveNext() => false;

            public void Reset() { }

            public Core.Bloom Current => null;
            
            public bool TryGetBlockRange(out Range<long> blockRange)
            {
                blockRange = default;
                return false;
            }

            object IEnumerator.Current => Current;

            public void Dispose() { }
        }
    }
}