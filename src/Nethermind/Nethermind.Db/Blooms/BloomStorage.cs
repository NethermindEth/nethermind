// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            Levels = (byte)_storageLevels.Length;
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

                if (levelsFromDb is null)
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
                    byte level = (byte)(configIndexLevelBucketSizes.Count - i - 1);
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
            var batchSize = _storageLevels.First().LevelElementSize;
            (BloomStorageLevel Level, Bloom Bloom)[] levelBlooms = _storageLevels.SkipLast(1).Select(l => (l, new Bloom())).ToArray();
            BloomStorageLevel lastLevel = _storageLevels.Last();

            long i = 0;
            long lastBlockNumber = -1;

            foreach (BlockHeader blockHeader in headers)
            {
                i++;

                Bloom blockHeaderBloom = blockHeader.Bloom ?? Bloom.Empty;
                lastLevel.Migrate(blockHeader.Number, blockHeaderBloom);

                for (int index = 0; index < levelBlooms.Length; index++)
                {
                    (BloomStorageLevel level, Bloom bloom) = levelBlooms[index];
                    bloom.Accumulate(blockHeaderBloom);

                    if (i % level.LevelElementSize == 0)
                    {
                        level.Migrate(blockHeader.Number, bloom);
                        levelBlooms[index] = (level, new Bloom());

                        if (level.LevelElementSize == batchSize)
                        {
                            MigratedBlockNumber += batchSize;
                        }
                    }
                }

                lastBlockNumber = blockHeader.Number;
            }

            if (i % batchSize != 0)
            {
                for (int index = 0; index < levelBlooms.Length; index++)
                {
                    (BloomStorageLevel level, Bloom bloom) = levelBlooms[index];
                    level.Store(lastBlockNumber, bloom);
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
            // ReSharper disable InconsistentNaming
            private readonly byte Level;
            public readonly int LevelElementSize;
            private readonly int LevelMultiplier;
            public readonly Average Average = new();
            // ReSharper restore InconsistentNaming

            private readonly IFileStore _fileStore;
            private readonly bool _migrationStatistics;
            private readonly LruCache<long, Bloom> _cache;

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

                try
                {
                    lock (_fileStore)
                    {
                        Bloom? existingBloom = _cache.Get(bucket);
                        if (existingBloom is null)
                        {
                            byte[] bytes = new byte[Bloom.ByteLength];
                            int bytesRead = _fileStore.Read(bucket, bytes);
                            bool bloomRead = bytesRead == Bloom.ByteLength;
                            existingBloom = bloomRead ? new Bloom(bytes) : new Bloom();
                        }

                        existingBloom.Accumulate(bloom);

                        _fileStore.Write(bucket, existingBloom.Bytes);
                        _cache.Set(bucket, existingBloom);
                    }
                }
                catch (InvalidOperationException e)
                {
                    InvalidOperationException exception = new(e.Message + $" Trying to write bloom index for block {blockNumber} at bucket {bucket}", e)
                    {
                        Data = { { "Bucket", bucket }, { "Block", blockNumber } }
                    };

                    throw exception;
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
                    BloomStorageLevel level = storageLevels[i];

                    if (i != storageLevels.Length - 1)
                    {
                        long fromBucket = level.GetBucket(fromBlock);
                        long toBucket = level.GetBucket(toBlock);
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
                    BloomStorageLevel currentStorageLevel = _storageLevels[CurrentLevel].Storage;
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
                    BloomStorageLevel level = _storageLevels[_currentLevel].Storage;
                    long bucket = level.GetBucket(_currentPosition);
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
                (long FromBlock, long ToBlock) indices = CurrentIndices;
                return $"From: {_fromBlock}, To: {_toBlock}, MaxLevel {_maxLevel}, CurrentBloom {indices.FromBlock}...{indices.ToBlock}, CurrentLevelSize {indices.ToBlock - indices.FromBlock + 1} CurrentLevel {CurrentLevel}, LevelRead {_currentLevelRead}";
            }
        }
    }
}
