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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;
using Nethermind.Store;

namespace Nethermind.Blockchain.Bloom
{
    public interface IBloomStorage
    {
        void Store(long blockNumber, Core.Bloom bloom);

        IBloomEnumerator GetBlooms(long fromBlock, long toBlock);
    }

    public class BloomStorage : IBloomStorage
    {
        private readonly BloomStorageLevel[] _storageLevels;
        
        private const int LevelMultiplier = 16;

        public BloomStorage(IColumnDb<byte> columnDb)
        {
            _storageLevels = columnDb.ColumnDbs.Select(k => CreateLevel(k.Value, k.Key, columnDb.ColumnDbs.Count)).ToArray();
        }

        private BloomStorageLevel CreateLevel(IDb db, byte level, in int levelCount) => new BloomStorageLevel(db, level, (int) Math.Pow(LevelMultiplier, levelCount - level + 1));

        public void Store(long blockNumber, Core.Bloom bloom)
        {
            for (int i = 0; i < _storageLevels.Length; i++)
            {
                _storageLevels[i].Store(blockNumber, bloom);
            }
        }

        public IBloomEnumerator GetBlooms(long fromBlock, long toBlock)
        {
            return new BloomEnumerator(_storageLevels, fromBlock, toBlock);
        }
        
        private class BloomStorageLevel
        {
            private readonly IDb _db;
            private readonly byte _level;
            private readonly int _levelElementSize;
            private readonly LruCache<long, Core.Bloom> _cache = new LruCache<long, Core.Bloom>(LevelMultiplier);

            public BloomStorageLevel(IDb db, in byte level, int levelElementSize)
            {
                _db = db;
                _level = level;
                _levelElementSize = levelElementSize;
            }

            public void Store(in long blockNumber, Core.Bloom bloom)
            {
                long bucket = blockNumber / _levelElementSize;
                var key = bucket.ToBigEndianByteArrayWithoutLeadingZeros();
                
                var existingBloom = _cache.Get(bucket);
                if (existingBloom == null)
                {
                    var bytes = _db[key];
                    existingBloom = bytes == null ? Core.Bloom.Empty : new Core.Bloom(bytes);
                }
                
                existingBloom.Accrue(bloom);
                
                _db[key] = existingBloom.Bytes;
                _cache.Set(bucket, existingBloom);
            }
        }
        
        private class BloomEnumerator : IBloomEnumerator
        {
            private readonly BloomStorageLevel[] _storageLevels;
            private readonly long _fromBlock;
            private readonly long _toBlock;
            private long _currentPosition;
            private byte _currentLevel = 0;

            public BloomEnumerator(BloomStorageLevel[] storageLevels, in long fromBlock, in long toBlock)
            {
                _storageLevels = storageLevels;
                _fromBlock = fromBlock;
                _toBlock = toBlock;
                _currentPosition = fromBlock - 1;
            }

            public bool MoveNext()
            {
                if (_currentPosition < _toBlock)
                {
                    
                }
            }

            public void Reset()
            {
                _currentPosition = _fromBlock - 1;
            }

            public Core.Bloom Current
            {
                get
                {
                    if (_currentPosition < _fromBlock || _currentPosition > _toBlock)
                    {
                        return null;
                    }
                    else
                    {
                        return ??;
                    }
                }
            }

            public bool TryGetBlockNumber(out long blockNumber)
            {
                throw new NotImplementedException();
            }

            object IEnumerator.Current => Current;

            public void Dispose() { }
        }
    }

    public interface IBloomEnumerator : IEnumerator<Core.Bloom>
    {
        bool TryGetBlockNumber(out long blockNumber);
    }
}