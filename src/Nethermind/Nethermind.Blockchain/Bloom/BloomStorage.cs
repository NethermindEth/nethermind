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
using System.Linq;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Store;

namespace Nethermind.Blockchain.Bloom
{
    public class BloomStorage : IBloomStorage
    {
        private const int LevelMultiplier = 16;
        private const int Levels = 3;
        private static readonly Keccak MinBlockNumberKey = Keccak.Compute(nameof(MinBlockNumber));
        private static readonly Keccak MaxBlockNumberKey = Keccak.Compute(nameof(MaxBlockNumber));
        
        private readonly BloomStorageLevel[] _storageLevels;
        private readonly IColumnsDb<byte> _bloomDb;
        
        public long MinBlockNumber { get; private set; }

        public long MaxBlockNumber { get; private set; }

        public BloomStorage(IColumnsDb<byte> bloomDb)
        {
            long Get(Keccak key, long defaultValue)
            {
                var bytes = _bloomDb.Get(key);
                return bytes?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? defaultValue;
            }
            
            _bloomDb = bloomDb;
            _storageLevels = Enumerable.Range(0, Levels).Cast<byte>().Select(level => CreateLevel(_bloomDb.GetColumnDb(level), level, Levels)).ToArray();
            MinBlockNumber = Get(MinBlockNumberKey, long.MaxValue);
            MaxBlockNumber = Get(MaxBlockNumberKey, -1);
        }

        private BloomStorageLevel CreateLevel(IDb db, byte level, in int levelCount) => new BloomStorageLevel(db, level, (int) Math.Pow(LevelMultiplier, levelCount - level + 1));

        public void Store(long blockNumber, Core.Bloom bloom)
        {
            void Set(Keccak key, long value)
            {
                _bloomDb.Set(key, value.ToBigEndianByteArrayWithoutLeadingZeros());
            }
            
            for (int i = 0; i < _storageLevels.Length; i++)
            {
                _storageLevels[i].Store(blockNumber, bloom);
            }

            if (blockNumber < MinBlockNumber)
            {
                MinBlockNumber = blockNumber;
                Set(MinBlockNumberKey, MinBlockNumber);
            }

            if (blockNumber > MaxBlockNumber)
            {
                MaxBlockNumber = blockNumber;
                Set(MaxBlockNumberKey, MaxBlockNumber);
            }
        }

        public IBloomEnumerator GetBlooms(long fromBlock, long toBlock) => new BloomEnumerator(_storageLevels, Math.Max(fromBlock, MinBlockNumber), Math.Min(toBlock, MaxBlockNumber));

        private class BloomStorageLevel
        {
            private readonly IDb _db;
            private readonly byte _level;
            public readonly int LevelElementSize;
            private readonly LruCache<long, Core.Bloom> _cache = new LruCache<long, Core.Bloom>(LevelMultiplier);

            public BloomStorageLevel(IDb db, in byte level, int levelElementSize)
            {
                _db = db;
                _level = level;
                LevelElementSize = levelElementSize;
            }

            public void Store(in long blockNumber, Core.Bloom bloom)
            {
                long bucket = GetBucket(blockNumber);
                
                var existingBloom = _cache.Get(bucket);
                if (existingBloom == null)
                {
                    var bytes = _db.Get(bucket);
                    existingBloom = bytes == null ? Core.Bloom.Empty : new Core.Bloom(bytes);
                }
                
                existingBloom.Accrue(bloom);
                
                _db.Set(bucket, existingBloom.Bytes);;
                _cache.Set(bucket, existingBloom);
            }

            private long GetBucket(long blockNumber) => blockNumber / LevelElementSize;

            public Core.Bloom Get(in long blockNumber)
            {
                long bucket = GetBucket(blockNumber);
                var bloom = _cache.Get(bucket);
                if (bloom == null)
                {
                    var bytes = _db.Get(bucket);
                    if (bytes == null)
                    {
                        return null;
                    }

                    bloom = new Core.Bloom(bytes);
                    _cache.Set(bucket, bloom);
                }

                return bloom;
            }
        }
        
        private class BloomEnumerator : IBloomEnumerator
        {
            private readonly BloomStorageLevel[] _storageLevels;
            private readonly long _fromBlock;
            private readonly long _toBlock;
            private readonly int _maxLevel;
            private long _currentPosition;
            private byte _currentLevel;
            
            public BloomEnumerator(BloomStorageLevel[] storageLevels, in long fromBlock, in long toBlock)
            {
                _storageLevels = storageLevels;
                _fromBlock = fromBlock;
                _toBlock = toBlock;
                _maxLevel = storageLevels.Length - 1;
                Reset();
            }

            public bool MoveNext()
            {
                _currentPosition += _storageLevels[_currentLevel].LevelElementSize;
                if (_currentLevel != 0 && _currentPosition % _storageLevels[_currentLevel - 1].LevelElementSize == 0)
                {
                    _currentLevel--;
                }
                return _currentPosition <= _toBlock;
            }

            public void Reset()
            {
                _currentPosition = _fromBlock / _storageLevels[0].LevelElementSize - _storageLevels[0].LevelElementSize;
            }

            public Core.Bloom Current => _currentPosition < _fromBlock || _currentPosition > _toBlock 
                ? null 
                : _storageLevels[_currentLevel].Get(_currentPosition);

            public bool TryGetBlockRange(out Range<long> blockRange)
            {
                if (_currentLevel == _maxLevel)
                {
                    blockRange = new Range<long>(Math.Max(_currentPosition, _fromBlock), Math.Min(_currentPosition + _storageLevels[_currentLevel].LevelElementSize, _toBlock));
                    return true;
                }

                _currentLevel++;
                blockRange = default;
                return false;
            }

            object IEnumerator.Current => Current;

            public void Dispose() { }
        }
    }
}