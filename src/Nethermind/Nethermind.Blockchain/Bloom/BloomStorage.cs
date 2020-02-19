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
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Store;

namespace Nethermind.Blockchain.Bloom
{
    public class BloomStorage : IBloomStorage
    {
        public byte Levels { get; private set; }
        public int MaxBucketSize => _storageLevels.FirstOrDefault()?.LevelElementSize ?? 1;

        internal static readonly Keccak MinBlockNumberKey = Keccak.Compute(nameof(MinBlockNumber));
        internal static readonly Keccak MaxBlockNumberKey = Keccak.Compute(nameof(MaxBlockNumber));
        internal static readonly Keccak MigrationBlockNumberKey = Keccak.Compute(nameof(MigratedBlockNumber));
        
        private readonly BloomStorageLevel[] _storageLevels;
        private readonly IBloomConfig _config;
        private readonly IDb _bloomInfoDb;
        private readonly IFileStoreFactory _fileStoreFactory;
        
        public long MinBlockNumber { get; private set; }
        private long MaxBlockNumber { get; set; }
        public long MigratedBlockNumber { get; private set; }

        public BloomStorage(IBloomConfig config, IDb bloomDb, IFileStoreFactory fileStoreFactory)
        {
            long Get(Keccak key, long defaultValue) => bloomDb.Get(key)?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? defaultValue;
            
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _bloomInfoDb = bloomDb ?? throw new ArgumentNullException(nameof(_bloomInfoDb));
            _fileStoreFactory = fileStoreFactory;
            SetLevels(config);
            _storageLevels = CreateStorageLevels(config);
            MinBlockNumber = Get(MinBlockNumberKey, long.MaxValue);
            MaxBlockNumber = Get(MaxBlockNumberKey, -1);
            MigratedBlockNumber = Get(MigrationBlockNumberKey, -1);
        }

        private BloomStorageLevel[] CreateStorageLevels(IBloomConfig config)
        {
            var lastLevelSize = 1;

            var configIndexLevelBucketSizes = config.IndexLevelBucketSizes.ToList();
            if (configIndexLevelBucketSizes.FirstOrDefault() != 1)
            { 
                configIndexLevelBucketSizes.Insert(0, 1);
                Levels++;
            }

            var maxElementSize = configIndexLevelBucketSizes.Aggregate(1, (i, b) => i * b);

            return configIndexLevelBucketSizes
                .Select((size, i) =>
                {
                    byte level = (byte) (Levels - i - 1);
                    var levelElementSize = lastLevelSize * size;
                    lastLevelSize = levelElementSize;
                    return new BloomStorageLevel(_fileStoreFactory.Create(level.ToString()), level, levelElementSize, size, _config.MigrationStatistics);
                })
                .Reverse()
                .ToArray();
        }

        private void SetLevels(IBloomConfig config)
        {
            Levels = (byte) config.IndexLevelBucketSizes.Length;

            if (Levels == 0)
            {
                throw new ArgumentException($"Can not create bloom index when there are no {nameof(config.IndexLevelBucketSizes)} provided.");
            }
        }
        
        private bool Contains(long blockNumber) => blockNumber >= MinBlockNumber && blockNumber <= MaxBlockNumber;
        
        public bool ContainsRange(in long fromBlockNumber, in long toBlockNumber) => Contains(fromBlockNumber) && Contains(toBlockNumber);

        public IEnumerable<Average> Averages => _storageLevels.Select(l => l.Average);

        public void Store(long blockNumber, Core.Bloom bloom)
        {
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

        public void StoreMigration(IEnumerable<BlockHeader> headers)
        {
            var batchSize =_storageLevels.First().LevelElementSize;
            (BloomStorageLevel Level, Core.Bloom Bloom)[] levelBlooms = _storageLevels.SkipLast(1).Select(l => (l, new Core.Bloom())).ToArray();
            BloomStorageLevel lastLevel = _storageLevels.Last();
            
            long i = 0;
            long lastBlockNumber = -1;
            
            foreach (var blockHeader in headers)
            {
                i++;

                var blockHeaderBloom = blockHeader.Bloom ?? Core.Bloom.Empty;
                lastLevel.StoreMigration(blockHeader.Number, blockHeaderBloom);
                
                for (var index = 0; index < levelBlooms.Length; index++)
                {
                    var levelBloom = levelBlooms[index];
                    levelBloom.Bloom.Accrue(blockHeaderBloom);
                    
                    if (i % levelBloom.Level.LevelElementSize == 0)
                    {
                        levelBloom.Level.StoreMigration(blockHeader.Number, levelBloom.Bloom);
                        levelBlooms[index] = (levelBloom.Level, new Core.Bloom());

                        if (levelBloom.Level.LevelElementSize == batchSize)
                        {
                            MigratedBlockNumber += batchSize;
                            Set(MigrationBlockNumberKey, MigratedBlockNumber);
                        }
                    }
                }

                lastBlockNumber = blockHeader.Number;
            }

            if (i % batchSize != 0)
            {
                for (var index = 0; index < levelBlooms.Length; index++)
                {
                    var levelBloom = levelBlooms[index];
                    levelBloom.Level.Store(lastBlockNumber, levelBloom.Bloom);
                }
                
                MigratedBlockNumber += i;
                Set(MigrationBlockNumberKey, MigratedBlockNumber);
            }
            
            if (MigratedBlockNumber >= MinBlockNumber - 1)
            {
                MinBlockNumber = 0;
                Set(MinBlockNumberKey, MinBlockNumber);
            }
        }

        private void Set(Keccak key, long value)
        {
            _bloomInfoDb.Set(key, value.ToBigEndianByteArrayWithoutLeadingZeros());
        }

        public void Dispose()
        {
            for (int i = 0; i < _storageLevels.Length; i++)
            {
                _storageLevels[i].Dispose();
            }
        }

        public IBloomEnumeration GetBlooms(long fromBlock, long toBlock) => new BloomEnumeration(_storageLevels, Math.Max(fromBlock, MinBlockNumber), Math.Min(toBlock, MaxBlockNumber));

        private class BloomStorageLevel : IDisposable
        {
            public readonly byte Level;
            public readonly int LevelElementSize;
            public readonly Average Average = new Average();
            
            private readonly IFileStore _fileStore;
            private readonly bool _migrationStatistics;
            private readonly LruCache<long, Core.Bloom> _cache;
            private readonly byte[] _bytes = new byte[Core.Bloom.ByteLength];

            public BloomStorageLevel(IFileStore fileStore, in byte level, in int levelElementSize, in int levelMultiplier, bool migrationStatistics)
            {
                _fileStore = fileStore;
                Level = level;
                LevelElementSize = levelElementSize;
                _migrationStatistics = migrationStatistics;
                _cache = new LruCache<long, Core.Bloom>(levelMultiplier);
            }

            public void Store(long blockNumber, Core.Bloom bloom)
            {
                long bucket = GetBucket(blockNumber);
                
                var existingBloom = _cache.Get(bucket);
                if (existingBloom == null)
                {
                    var bytesRead = _fileStore.Read(bucket, _bytes);
                    var bloomRead = bytesRead == Core.Bloom.ByteLength;
                    existingBloom = bloomRead ? new Core.Bloom(_bytes) : new Core.Bloom();
                }

                existingBloom.Accrue(bloom);

                _fileStore.Write(bucket, existingBloom.Bytes);
                _cache.Set(bucket, existingBloom);
            }
            

            private static uint CountBits(Core.Bloom bloom) => (uint) bloom.Bytes.AsSpan().CountBits();

            public long GetBucket(long blockNumber) => blockNumber / LevelElementSize;

            public IFileReader GetReader() => _fileStore.GetFileReader();

            public void StoreMigration(in long blockNumber, Core.Bloom bloom)
            {
                if (_migrationStatistics)
                {
                    Average.Add(CountBits(bloom));
                }
                        
                _fileStore.Write(GetBucket(blockNumber), bloom.Bytes);
            }

            public void Dispose()
            {
                _fileStore?.Dispose();
            }
        }
        
        private class BloomEnumeration : IBloomEnumeration
        {
            private readonly BloomStorageLevel[] _storageLevels;
            private readonly long _fromBlock;
            private readonly long _toBlock;
            private BloomEnumerator _current;

            public BloomEnumeration(BloomStorageLevel[] storageLevels, long fromBlock, long toBlock)
            {
                _storageLevels = storageLevels;
                _fromBlock = fromBlock;
                _toBlock = toBlock;
            }
            
            public IEnumerator<Core.Bloom> GetEnumerator() => _current = new BloomEnumerator(_storageLevels, _fromBlock, _toBlock);

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public bool TryGetBlockNumber(out long blockNumber) => _current.TryGetBlockNumber(out blockNumber);
        }
        
        private class BloomEnumerator : IEnumerator<Core.Bloom>
        {
            private readonly (BloomStorageLevel Storage, IFileReader Reader)[] _storageLevels;
            private readonly long _fromBlock;
            private readonly long _toBlock;
            private readonly int _maxLevel;
            private long _currentPosition;
            // private byte[] bytes = new byte[Core.Bloom.ByteLength];
            private Core.Bloom _bloom = new Core.Bloom();
            
            private byte CurrentLevel
            {
                get => _currentLevel;
                set
                {
                    _currentLevel = value;
                    _currentLevelRead = false;
                }
            }

            private bool _currentLevelRead;
            private byte _currentLevel;

            public BloomEnumerator(BloomStorageLevel[] storageLevels, in long fromBlock, in long toBlock)
            {
                _storageLevels = storageLevels.Select(l => (l, l.GetReader())).ToArray();
                _fromBlock = fromBlock;
                _toBlock = toBlock;
                _maxLevel = storageLevels.Length - 1;
                Reset();
            }

            public void Reset()
            {
                _currentPosition = _fromBlock;
                CurrentLevel = 0;
            }

            object? IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_currentLevelRead)
                {
                    _currentPosition += _storageLevels[CurrentLevel].Storage.LevelElementSize;
                    while (CurrentLevel > 0 && _currentPosition % _storageLevels[CurrentLevel - 1].Storage.LevelElementSize == 0)
                    {
                        CurrentLevel--;
                    }
                }
                else
                {
                    _currentLevelRead = true;
                }

                return _currentPosition <= _toBlock;
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
                        var storageLevel = _storageLevels[CurrentLevel];
                        storageLevel .Reader.Read(storageLevel.Storage.GetBucket(_currentPosition), _bloom.Bytes);
                        return _bloom;
                    }
                }
            }

            public bool TryGetBlockNumber(out long blockNumber)
            {
                if (CurrentLevel == _maxLevel)
                {
                    blockNumber = _currentPosition;
                    return true;
                }

                CurrentLevel++;
                blockNumber = default;
                return false;
            }
            
            public void Dispose()
            { 
                foreach (var storageLevel in _storageLevels)
                {
                    storageLevel.Reader.Dispose();
                }  
            }
        }
    }
}