// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Repositories
{
    public class ChainLevelInfoRepository([KeyFilter(DbNames.BlockInfos)] IDb blockInfoDb) : IChainLevelInfoRepository, IClearableCache
    {
        // Above the header cache's capacity, so a number lookup for a recent or cached block rarely falls through to the db.
        private const int CacheSize = 512;

        private readonly object _writeLock = new();
        private long _deletions;
        private readonly AssociativeCache<LevelNumber, ChainLevelInfo> _blockInfoCache = new(CacheSize);
        private readonly IRlpDecoder<ChainLevelInfo> _decoder = Rlp.GetDecoder<ChainLevelInfo>()
            ?? throw new InvalidOperationException($"No RLP decoder is registered for {nameof(ChainLevelInfo)}.");

        private readonly IDb _blockInfoDb = blockInfoDb ?? throw new ArgumentNullException(nameof(blockInfoDb));

        public void Delete(ulong number, BatchWrite? batch = null)
        {
            LevelNumber key = new(number);
            if (batch is null || batch.Disposed)
            {
                lock (_writeLock)
                {
                    _blockInfoCache.Delete(in key);
                    _blockInfoDb.Delete(number);
                    Interlocked.Increment(ref _deletions);
                    // A load that read the level before the db delete may have cached it since.
                    _blockInfoCache.Delete(in key);
                }
            }
            else
            {
                Interlocked.Increment(ref _deletions);
                _blockInfoCache.Delete(in key);
                batch.WriteBatch.Delete(number);
            }
        }

        public void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null)
        {
            void LocalPersistLevel()
            {
                _blockInfoCache.Set(new LevelNumber(number), level);
                using ArrayPoolSpan<byte> rlp = _decoder.EncodeToArrayPoolSpan(level);
                _blockInfoDb.PutSpan(number.ToBigEndianSpanWithoutLeadingZeros(out _), rlp);
            }

            if (batch is null || batch.Disposed)
            {
                lock (_writeLock)
                {
                    LocalPersistLevel();
                }
            }
            else
            {
                _blockInfoCache.Set(new LevelNumber(number), level);
                using ArrayPoolSpan<byte> rlp = _decoder.EncodeToArrayPoolSpan(level);
                batch.WriteBatch.PutSpan(number.ToBigEndianSpanWithoutLeadingZeros(out _), rlp);
            }
        }

        public BatchWrite StartBatch() => new(_writeLock, _blockInfoDb.StartWriteBatch);

        public ChainLevelInfo? LoadLevel(ulong number)
        {
            LevelNumber key = new(number);
            if (_blockInfoCache.TryGet(in key, out ChainLevelInfo? level)) return level;

            long deletions = Volatile.Read(ref _deletions);
            level = _blockInfoDb.Get(number, _decoder);
            if (level is null) return null;

            if (_blockInfoCache.TryAdd(in key, level))
            {
                // A delete that ran during this load may already have cleared the cache, so the level read before it
                // must not stay cached. The barrier orders the cache insert before the counter read, against Delete's
                // increment before its last cache removal. Only this load's own entry is withdrawn: a level a writer
                // cached since, possibly in a batch not yet committed, stays.
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _deletions) != deletions) _blockInfoCache.TryRemove(in key, level);
            }
            else if (_blockInfoCache.TryGetNoRefresh(in key, out ChainLevelInfo? cached))
            {
                // A level persisted while this load ran is newer than the one read.
                level = cached;
            }

            return level;
        }

        public IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers)
        {
            byte[][] keys = new byte[blockNumbers.Count][];
            for (int i = 0; i < blockNumbers.Count; i++)
            {
                keys[i] = blockNumbers[i].ToBigEndianByteArrayWithoutLeadingZeros();
            }

            KeyValuePair<byte[], byte[]?>[] data = _blockInfoDb[keys];

            return data.Select(kv =>
                {
                    if (kv.Value is null || kv.Value.Length == 0) return null;
                    RlpReader reader = new(kv.Value);
                    return _decoder.Decode(ref reader, RlpBehaviors.AllowExtraBytes);
                })
                .ToPooledList(data.Length);
        }

        void IClearableCache.ClearCache() => _blockInfoCache.Clear();

        private readonly struct LevelNumber(ulong number) : IHash64bit<LevelNumber>
        {
            private const ulong GoldenRatio = 0x9E3779B97F4A7C15;

            private readonly ulong _number = number;

            // The cache picks the set from the low bits: rotating the product brings its well-mixed middle bits there,
            // so consecutive levels and strided ones both spread evenly over the sets.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public long GetHashCode64() => (long)BitOperations.RotateLeft(_number * GoldenRatio, 32);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Equals(in LevelNumber other) => _number == other._number;
        }
    }
}
