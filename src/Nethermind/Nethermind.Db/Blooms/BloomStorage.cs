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
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Db.Blooms
{
    public class BloomStorage : IBloomStorage
    {
        public byte Levels { get; private set; }
        public int MaxBucketSize => _storageLevels.FirstOrDefault()?.LevelElementSize ?? 1;

        internal static readonly Keccak MinBlockNumberKey = Keccak.Compute(nameof(MinBlockNumber));
        internal static readonly Keccak MaxBlockNumberKey = Keccak.Compute(nameof(MaxBlockNumber));
        private static readonly Keccak MigrationBlockNumberKey = Keccak.Compute(nameof(MigratedBlockNumber));
        private static readonly Keccak LevelsKey = Keccak.Compute(nameof(LevelsKey));
        
        private readonly BloomStorageLevel[] _storageLevels;
        private readonly IBloomConfig _config;
        private readonly IDb _bloomInfoDb;
        private readonly IFileStoreFactory _fileStoreFactory;
        private long _minBlockNumber;
        private long _maxBlockNumber;
        private long _migratedBlockNumber;

        public long MinBlockNumber
        {
            get => _minBlockNumber;
            private set
            {
                _minBlockNumber = value;
                Set(MinBlockNumberKey, MinBlockNumber);
            }
        }

        public long MaxBlockNumber
        {
            get => _maxBlockNumber;
            set
            {
                _maxBlockNumber = value;
                Set(MaxBlockNumberKey, MaxBlockNumber);
            }
        }

        public long MigratedBlockNumber
        {
            get => _migratedBlockNumber;
            private set
            {
                _migratedBlockNumber = value;
                Set(MigrationBlockNumberKey, MigratedBlockNumber);
            }
        }

        public BloomStorage(IBloomConfig config, IDb bloomDb, IFileStoreFactory fileStoreFactory)
        {
            long Get(Keccak key, long defaultValue) => bloomDb.Get(key)?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? defaultValue;
            
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _bloomInfoDb = bloomDb ?? throw new ArgumentNullException(nameof(_bloomInfoDb));
            _fileStoreFactory = fileStoreFactory;
            _storageLevels = CreateStorageLevels(config);
            Levels = (byte) _storageLevels.Length;
            _minBlockNumber = Get(MinBlockNumberKey, long.MaxValue);
            _maxBlockNumber = Get(MaxBlockNumberKey, -1);
            _migratedBlockNumber = Get(MigrationBlockNumberKey, -1);
        }

        private BloomStorageLevel[] CreateStorageLevels(IBloomConfig config)
        {
            void ValidateConfigValue()
            {
                if (config.IndexLevelBucketSizes.Length == 0)
                {
                    throw new ArgumentException($"Can not create bloom index when there are no {nameof(config.IndexLevelBucketSizes)} provided.", nameof(config.IndexLevelBucketSizes));
                }
            }
            
            List<int> InsertBaseLevelIfNeeded()
            {
                List<int> sizes = config.IndexLevelBucketSizes.ToList();
                if (sizes.FirstOrDefault() != 1)
                {
                    sizes.Insert(0, 1);
                }

                return sizes;
            }
            
            void ValidateCurrentDbStructure(IList<int> sizes)
            {
                var levelsFromDb = _bloomInfoDb.Get(LevelsKey);

                if (levelsFromDb == null)
                {
                    _bloomInfoDb.Set(LevelsKey, Rlp.Encode(sizes.ToArray()).Bytes);
                }
                else
                {
                    var stream = new RlpStream(levelsFromDb);
                    var dbBucketSizes = stream.DecodeArray(x => x.DecodeInt());

                    if (!dbBucketSizes.SequenceEqual(sizes))
                    {
                        throw new ArgumentException($"Can not load bloom db. {nameof(config.IndexLevelBucketSizes)} changed without rebuilding bloom db. Db structure is [{string.Join(",", dbBucketSizes)}]. Current config value is [{string.Join(",", sizes)}]. " +
                                                    $"If you want to rebuild {DbNames.Bloom} db, please delete db folder. If not, please change config value to reflect current db structure", nameof(config.IndexLevelBucketSizes));
                    }
                }
            }

            ValidateConfigValue();
            var configIndexLevelBucketSizes = InsertBaseLevelIfNeeded();
            ValidateCurrentDbStructure(configIndexLevelBucketSizes);

            var lastLevelSize = 1;
            return configIndexLevelBucketSizes
                .Select((size, i) =>
                {
                    byte level = (byte) (configIndexLevelBucketSizes.Count - i - 1);
                    var levelElementSize = lastLevelSize * size;
                    lastLevelSize = levelElementSize;
                    return new BloomStorageLevel(_fileStoreFactory.Create(level.ToString()), level, levelElementSize, size, _config.MigrationStatistics);
                })
                .Reverse()
                .ToArray();
        }

        private bool Contains(long blockNumber) => blockNumber >= MinBlockNumber && blockNumber <= MaxBlockNumber;
        
        public bool ContainsRange(in long fromBlockNumber, in long toBlockNumber) => Contains(fromBlockNumber) && Contains(toBlockNumber);

        public IEnumerable<Average> Averages => _storageLevels.Select(l => l.Average);

        public void Store(long blockNumber, Bloom bloom)
        {
            for (int i = 0; i < _storageLevels.Length; i++)
            {
                _storageLevels[i].Store(blockNumber, bloom);
            }

            if (blockNumber < MinBlockNumber)
            {
                MinBlockNumber = blockNumber;
            }

            if (blockNumber > MaxBlockNumber)
            {
                MaxBlockNumber = blockNumber;
            }
        }

        public void Migrate(IEnumerable<BlockHeader> headers)
        {
            var batchSize =_storageLevels.First().LevelElementSize;
            (BloomStorageLevel Level, Bloom Bloom)[] levelBlooms = _storageLevels.SkipLast(1).Select(l => (l, new Bloom())).ToArray();
            BloomStorageLevel lastLevel = _storageLevels.Last();
            
            long i = 0;
            long lastBlockNumber = -1;
            
            foreach (var blockHeader in headers)
            {
                i++;

                var blockHeaderBloom = blockHeader.Bloom ?? Bloom.Empty;
                lastLevel.Migrate(blockHeader.Number, blockHeaderBloom);
                
                for (var index = 0; index < levelBlooms.Length; index++)
                {
                    var levelBloom = levelBlooms[index];
                    levelBloom.Bloom.Accumulate(blockHeaderBloom);
                    
                    if (i % levelBloom.Level.LevelElementSize == 0)
                    {
                        levelBloom.Level.Migrate(blockHeader.Number, levelBloom.Bloom);
                        levelBlooms[index] = (levelBloom.Level, new Bloom());

                        if (levelBloom.Level.LevelElementSize == batchSize)
                        {
                            MigratedBlockNumber += batchSize;
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
            }

            if (MigratedBlockNumber >= MinBlockNumber - 1)
            {
                MinBlockNumber = 0;
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
            public readonly int LevelMultiplier;
            public readonly Average Average = new();
            
            private readonly IFileStore _fileStore;
            private readonly bool _migrationStatistics;
            private readonly ICache<long, Bloom> _cache;
            
            public BloomStorageLevel(IFileStore fileStore, in byte level, in int levelElementSize, in int levelMultiplier, bool migrationStatistics)
            {
                _fileStore = fileStore;
                Level = level;
                LevelElementSize = levelElementSize;
                LevelMultiplier = levelMultiplier;
                _migrationStatistics = migrationStatistics;
                _cache = new LruCache<long, Bloom>(levelMultiplier, levelMultiplier, "blooms");
            }

            public void Store(long blockNumber, Bloom bloom)
            {
                long bucket = GetBucket(blockNumber);

                lock (_fileStore)
                {
                    var existingBloom = _cache.Get(bucket);
                    if (existingBloom == null)
                    {
                        byte[] bytes = new byte[Bloom.ByteLength];
                        var bytesRead = _fileStore.Read(bucket, bytes);
                        var bloomRead = bytesRead == Bloom.ByteLength;
                        existingBloom = bloomRead ? new Bloom(bytes) : new Bloom();
                    }

                    existingBloom.Accumulate(bloom);

                    _fileStore.Write(bucket, existingBloom.Bytes);
                    _cache.Set(bucket, existingBloom);
                }
            }
            
            private static uint CountBits(Bloom bloom) => bloom.Bytes.AsSpan().CountBits();

            public long GetBucket(long blockNumber) => blockNumber / LevelElementSize;

            public IFileReader CreateReader() => _fileStore.CreateFileReader();

            public void Migrate(in long blockNumber, Bloom bloom)
            {
                if (_migrationStatistics)
                {
                    Average.Increment(CountBits(bloom));
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
            
            public IEnumerator<Bloom> GetEnumerator() => _current = new BloomEnumerator(_storageLevels, _fromBlock, _toBlock);

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            public bool TryGetBlockNumber(out long blockNumber) => _current.TryGetBlockNumber(out blockNumber);
            public (long FromBlock, long ToBlock) CurrentIndices => _current.CurrentIndices;

            public override string ToString() => _current.ToString();
        }
        
        private class BloomEnumerator : IEnumerator<Bloom>
        {
            private readonly (BloomStorageLevel Storage, IFileReader Reader)[] _storageLevels;
            private readonly long _fromBlock;
            private readonly long _toBlock;
            private readonly int _maxLevel;
            private long _currentPosition;
            private readonly Bloom _bloom = new();
            
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
                _storageLevels = GetStorageLevels(storageLevels, fromBlock, toBlock);
                _fromBlock = fromBlock;
                _toBlock = toBlock;
                _maxLevel = _storageLevels.Length - 1;
                Reset();
            }

            private (BloomStorageLevel Storage, IFileReader Reader)[] GetStorageLevels(BloomStorageLevel[] storageLevels, long fromBlock, long toBlock)
            {
                // Skip higher levels if we would do only 1 or 2 lookups in them. Thanks to that we can skip a lot of IO operations on that file
                IList<BloomStorageLevel> levels = new List<BloomStorageLevel>(storageLevels.Length);
                for (int i = 0; i < storageLevels.Length; i++)
                {
                    var level = storageLevels[i];

                    if (i != storageLevels.Length - 1)
                    {
                        var fromBucket = level.GetBucket(fromBlock);
                        var toBucket = level.GetBucket(toBlock);
                        if (toBucket - fromBucket + 1 <= 2)
                        {
                            continue;
                        }
                    }
                    
                    levels.Add(level);
                }
                
                return levels.Select(l => (l, l.CreateReader())).AsParallel().ToArray();
            }

            public void Reset()
            {
                _currentPosition = _fromBlock;
                CurrentLevel = 0;
            }

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_currentLevelRead)
                {
                    var currentStorageLevel = _storageLevels[CurrentLevel].Storage;
                    _currentPosition += _currentPosition == _fromBlock
                        ? currentStorageLevel.LevelElementSize - _currentPosition % currentStorageLevel.LevelElementSize
                        : currentStorageLevel.LevelElementSize;
                    
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

            public Bloom Current
            {
                get
                {
                    if (_currentPosition < _fromBlock || _currentPosition > _toBlock)
                    {
                        return null;
                    }

                    var storageLevel = _storageLevels[CurrentLevel];
                    return storageLevel.Reader.Read(storageLevel.Storage.GetBucket(_currentPosition), _bloom.Bytes) == Bloom.ByteLength ? _bloom : Bloom.Empty;
                }
            }

            public (long FromBlock, long ToBlock) CurrentIndices
            {
                get
                {
                    var level = _storageLevels[_currentLevel].Storage;
                    var bucket = level.GetBucket(_currentPosition);
                    return (bucket * level.LevelElementSize, (bucket + 1) * level.LevelElementSize - 1);
                }
            }

            public bool TryGetBlockNumber(out long blockNumber)
            {
                blockNumber = _currentPosition;
                if (CurrentLevel == _maxLevel)
                {
                    return true;
                }

                CurrentLevel++;
                return false;
            }
            
            public void Dispose()
            { 
                foreach (var storageLevel in _storageLevels)
                {
                    storageLevel.Reader.Dispose();
                }  
            }

            public override string ToString()
            {
                var indices = CurrentIndices;
                return $"From: {_fromBlock}, To: {_toBlock}, MaxLevel {_maxLevel}, CurrentBloom {indices.FromBlock}...{indices.ToBlock}, CurrentLevelSize {indices.ToBlock - indices.FromBlock + 1} CurrentLevel {CurrentLevel}, LevelRead {_currentLevelRead}";
            }
        }
    }
}
