// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
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
        private readonly AssociativeCache<LevelNumber, EncodedLevel> _encodedLevelCache = new(8192);
        private long _encodedLevelGeneration;
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
                    InvalidateEncodedLevel(in key);
                    _blockInfoCache.Delete(in key);
                    _blockInfoDb.Delete(number);
                    Interlocked.Increment(ref _deletions);
                    // A load that read the level before the db delete may have cached it since.
                    _blockInfoCache.Delete(in key);
                }
            }
            else
            {
                InvalidateEncodedLevel(in key);
                Interlocked.Increment(ref _deletions);
                _blockInfoCache.Delete(in key);
                batch.WriteBatch.Delete(number);
            }
        }

        public void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null)
        {
            void LocalPersistLevel()
            {
                InvalidateEncodedLevel(new LevelNumber(number));
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
                InvalidateEncodedLevel(new LevelNumber(number));
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
            level = ReadLevel(number, in key);
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

        public ChainLevelInfo? LoadLevelWithoutCaching(ulong number)
        {
            LevelNumber key = new(number);
            if (_blockInfoCache.TryGetNoRefresh(in key, out ChainLevelInfo? level)) return level;

            level = ReadLevel(number, in key);
            // A level persisted while this load ran is newer than the one read.
            return _blockInfoCache.TryGetNoRefresh(in key, out ChainLevelInfo? cached) ? cached : level;
        }

        private ChainLevelInfo? ReadLevel(ulong number, in LevelNumber key)
        {
            bool canPublish = false;
            long generation = 0;
            byte[]? cachedRlp = null;
            // A batch holds the write lock through commit; reads admitted during it must not cache pre-commit rows.
            if (!Monitor.IsEntered(_writeLock) && Monitor.TryEnter(_writeLock))
            {
                try
                {
                    canPublish = true;
                    generation = _encodedLevelGeneration;
                    if (_encodedLevelCache.TryGet(in key, out EncodedLevel? cached)) cachedRlp = cached!.Rlp;
                }
                finally
                {
                    Monitor.Exit(_writeLock);
                }
            }

            if (cachedRlp is not null) return new LevelDecoder(_decoder).Decode((byte[])cachedRlp.Clone());

            ReadOnlySpan<byte> dbKey = number.ToBigEndianSpanWithoutLeadingZeros(out _);
            if (_blockInfoDb is IReadOnlyNativeKeyValueStore native)
            {
                ReadOnlySpan<byte> data = native.GetNativeSlice(dbKey, out nint handle);
                if (data.IsNull()) return null;
                try
                {
                    return DecodeAndCacheLevel(data, in key, canPublish, generation);
                }
                finally
                {
                    native.DangerousReleaseHandle(handle);
                }
            }

            Span<byte> span = _blockInfoDb.GetSpan(dbKey);
            if (span.IsNull()) return null;
            try
            {
                return DecodeAndCacheLevel(span, in key, canPublish, generation);
            }
            finally
            {
                _blockInfoDb.DangerousReleaseMemory(span);
            }
        }

        private ChainLevelInfo? DecodeAndCacheLevel(ReadOnlySpan<byte> rlp, in LevelNumber key, bool canPublish, long generation)
        {
            ChainLevelInfo? level = new LevelDecoder(_decoder).Decode(rlp);
            if (level is not null && canPublish && rlp.Length <= 1024)
            {
                byte[] snapshot = rlp.ToArray();
                if (!Monitor.IsEntered(_writeLock) && Monitor.TryEnter(_writeLock))
                {
                    try
                    {
                        if (generation == _encodedLevelGeneration) _encodedLevelCache.Set(in key, new EncodedLevel(snapshot));
                    }
                    finally
                    {
                        Monitor.Exit(_writeLock);
                    }
                }
            }
            return level;
        }

        private void InvalidateEncodedLevel(in LevelNumber key)
        {
            _encodedLevelGeneration++;
            _encodedLevelCache.Delete(in key);
        }

        public IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers)
        {
            int count = blockNumbers.Count;
            if (count == 0) return ArrayPoolList<ChainLevelInfo?>.Empty();

            int keyLength = GetKeyLength(blockNumbers[0]);
            for (int i = 1; i < count; i++)
            {
                if (GetKeyLength(blockNumbers[i]) != keyLength)
                    return ReadVariableLengthLevels(blockNumbers);
            }

            return ReadFixedLengthLevels(blockNumbers, keyLength);
        }

        private static int GetKeyLength(ulong number) =>
            sizeof(ulong) - Math.Min(BitOperations.LeadingZeroCount(number) / sizeof(ulong), sizeof(ulong) - 1);

        private IOwnedReadOnlyList<ChainLevelInfo?> ReadFixedLengthLevels(in ArrayPoolListRef<ulong> blockNumbers, int keyLength)
        {
            int count = blockNumbers.Count;
            int byteCount = checked(count * keyLength);
            if (byteCount <= 256)
                return ReadFixedLengthLevels(blockNumbers, keyLength, stackalloc byte[byteCount]);

            using ArrayPoolSpan<byte> keys = new(ArrayPool<byte>.Shared, byteCount);
            return ReadFixedLengthLevels(blockNumbers, keyLength, keys);
        }

        private IOwnedReadOnlyList<ChainLevelInfo?> ReadFixedLengthLevels(in ArrayPoolListRef<ulong> blockNumbers, int keyLength, Span<byte> keys)
        {
            int count = blockNumbers.Count;
            ArrayPoolList<ChainLevelInfo?> levels = new(count, count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    blockNumbers[i].ToBigEndianSpanWithoutLeadingZeros(out _).CopyTo(keys.Slice(i * keyLength, keyLength));
                }

                _blockInfoDb.GetMany<ChainLevelInfo?, LevelDecoder>(keys, keyLength, levels.AsSpan(), new LevelDecoder(_decoder));
                return levels;
            }
            catch
            {
                levels.Dispose();
                throw;
            }
        }

        private IOwnedReadOnlyList<ChainLevelInfo?> ReadVariableLengthLevels(in ArrayPoolListRef<ulong> blockNumbers)
        {
            byte[][] keys = new byte[blockNumbers.Count][];
            for (int i = 0; i < blockNumbers.Count; i++)
            {
                keys[i] = blockNumbers[i].ToBigEndianByteArrayWithoutLeadingZeros();
            }

            KeyValuePair<byte[], byte[]?>[] data = _blockInfoDb[keys];

            ArrayPoolList<ChainLevelInfo?> levels = new(data.Length, data.Length);
            LevelDecoder decoder = new(_decoder);
            try
            {
                for (int i = 0; i < data.Length; i++)
                {
                    levels[i] = data[i].Value is byte[] value ? decoder.Decode(value) : null;
                }

                return levels;
            }
            catch
            {
                levels.Dispose();
                throw;
            }
        }

        void IClearableCache.ClearCache()
        {
            lock (_writeLock)
            {
                _encodedLevelGeneration++;
                _encodedLevelCache.Clear();
                _blockInfoCache.Clear();
            }
        }

        private sealed class EncodedLevel(byte[] rlp)
        {
            public readonly byte[] Rlp = rlp;
        }

        private readonly struct LevelDecoder(IRlpDecoder<ChainLevelInfo> decoder) : IValueDecoder<ChainLevelInfo?>
        {
            public ChainLevelInfo? Decode(ReadOnlySpan<byte> buffer)
            {
                if (buffer.IsEmpty) return null;
                RlpReader reader = new(buffer);
                return decoder.Decode(ref reader, RlpBehaviors.AllowExtraBytes);
            }
        }

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
