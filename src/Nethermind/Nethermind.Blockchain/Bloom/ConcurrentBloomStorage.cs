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
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Blockchain.Bloom
{
    public class ConcurrentBloomStorage : IBloomStorage, IConcurrentStorage<long>
    {
        private readonly BloomStorage _innerStorage;
        private readonly object _switchLock = new object();
        private bool _concurrent = false;
        private readonly int _maxBucketSize;
        private Dictionary<int, object> _bucketLocks;

        public ConcurrentBloomStorage(BloomStorage innerStorage)
        {
            _innerStorage = innerStorage ?? throw new ArgumentException(nameof(innerStorage));
            _maxBucketSize = (int) Math.Pow(innerStorage.LevelMultiplier, innerStorage.Levels);
        }

        public void StartConcurrent(long context)
        {
            if (!_concurrent)
            {
                lock (_switchLock)
                {
                    if (!_concurrent)
                    {
                        _concurrent = true;

                        int bucketCount = (int) (context / _maxBucketSize + 1);
                        _bucketLocks = Enumerable.Range(0, bucketCount).ToDictionary(b => b, b => new object());
                    }
                }
            }
        }

        public void EndConcurrent(long context)
        {
            if (_concurrent)
            {
                lock (_switchLock)
                {
                    if (_concurrent)
                    {
                        _concurrent = false;
                        _bucketLocks = null;
                    }
                }
            }
        }

        public long MinBlockNumber => _innerStorage.MinBlockNumber;

        public void Store(long blockNumber, Core.Bloom bloom)
        {
            if (_concurrent && _bucketLocks.TryGetValue((int) (blockNumber / _maxBucketSize), out var bucketLock))
            {
                lock (bucketLock)
                {
                    _innerStorage.Store(blockNumber, bloom);
                }
            }
            else
            {
                _innerStorage.Store(blockNumber, bloom);
            }
        }

        public IBloomEnumeration GetBlooms(long fromBlock, long toBlock) => _innerStorage.GetBlooms(fromBlock, toBlock);

        public bool ContainsRange(in long fromBlockNumber, in long toBlockNumber) => _innerStorage.ContainsRange(in fromBlockNumber, in toBlockNumber);
    }
}