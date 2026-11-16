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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
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

        public ChainLevelInfo? LoadLevelWithoutCaching(ulong number)
        {
            LevelNumber key = new(number);
            if (_blockInfoCache.TryGetNoRefresh(in key, out ChainLevelInfo? level)) return level;

            level = _blockInfoDb.Get(number, _decoder);
            // A level persisted while this load ran is newer than the one read.
            return _blockInfoCache.TryGetNoRefresh(in key, out ChainLevelInfo? cached) ? cached : level;
        }

        public bool TryGetMainChainBlockWithoutCaching(ulong number, out ValueHash256 blockHash, out UInt256 totalDifficulty)
        {
            LevelNumber key = new(number);
            if (_blockInfoCache.TryGetNoRefresh(in key, out ChainLevelInfo? level)) return IChainLevelInfoRepository.TryGetMainChainBlock(level, out blockHash, out totalDifficulty);

            bool found = ReadMainChainBlock(number, out blockHash, out totalDifficulty);
            return _blockInfoCache.TryGetNoRefresh(in key, out ChainLevelInfo? cached)
                ? IChainLevelInfoRepository.TryGetMainChainBlock(cached, out blockHash, out totalDifficulty)
                : found;
        }

        private bool ReadMainChainBlock(ulong number, out ValueHash256 blockHash, out UInt256 totalDifficulty)
        {
            ReadOnlySpan<byte> key = number.ToBigEndianSpanWithoutLeadingZeros(out ulong _);
            if (_blockInfoDb is IReadOnlyNativeKeyValueStore native)
            {
                ReadOnlySpan<byte> slice = native.GetNativeSlice(key, out IntPtr handle);
                if (slice.IsNull()) return NotFound(out blockHash, out totalDifficulty);

                try
                {
                    return ReadMainChainBlock(slice, out blockHash, out totalDifficulty);
                }
                finally
                {
                    native.DangerousReleaseHandle(handle);
                }
            }

            Span<byte> data = _blockInfoDb.GetSpan(key);
            if (data.IsNull()) return NotFound(out blockHash, out totalDifficulty);

            try
            {
                return ReadMainChainBlock(data, out blockHash, out totalDifficulty);
            }
            finally
            {
                _blockInfoDb.DangerousReleaseMemory(data);
            }
        }

        private bool ReadMainChainBlock(ReadOnlySpan<byte> rlp, out ValueHash256 blockHash, out UInt256 totalDifficulty)
        {
            if (rlp.Length == 0) return NotFound(out blockHash, out totalDifficulty);
            if (ChainLevelDecoder.TryReadMainChainBlock(rlp, out bool hasMainChainBlock, out blockHash, out totalDifficulty)) return hasMainChainBlock;

            RlpReader reader = new(rlp);
            return IChainLevelInfoRepository.TryGetMainChainBlock(_decoder.Decode(ref reader, RlpBehaviors.AllowExtraBytes | RlpBehaviors.TrustedInput), out blockHash, out totalDifficulty);
        }

        /// <inheritdoc/>
        /// <remarks>The levels missing from the cache are read in one batch.</remarks>
        public IOwnedReadOnlyList<ChainLevelInfo?> LoadLevelsWithoutCaching(in ArrayPoolListRef<ulong> blockNumbers)
        {
            int count = blockNumbers.Count;
            ArrayPoolList<ChainLevelInfo?> levels = new(count, count);
            using ArrayPoolListRef<ulong> missed = new(count);
            using ArrayPoolListRef<int> positions = new(count);
            for (int i = 0; i < count; i++)
            {
                if (_blockInfoCache.TryGetNoRefresh(new LevelNumber(blockNumbers[i]), out ChainLevelInfo? level))
                {
                    levels[i] = level;
                }
                else
                {
                    missed.Add(blockNumbers[i]);
                    positions.Add(i);
                }
            }

            if (missed.Count == 0) return levels;

            using IOwnedReadOnlyList<ChainLevelInfo?> read = MultiLoadLevel(missed);
            for (int i = 0; i < missed.Count; i++)
            {
                // A level persisted while this load ran is newer than the one read.
                levels[positions[i]] = _blockInfoCache.TryGetNoRefresh(new LevelNumber(missed[i]), out ChainLevelInfo? cached) ? cached : read[i];
            }

            return levels;
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

        void IClearableCache.ClearCache() => _blockInfoCache.Clear();

        private readonly struct LevelDecoder(IRlpDecoder<ChainLevelInfo> decoder) : IValueDecoder<ChainLevelInfo?>
        {
            public ChainLevelInfo? Decode(ReadOnlySpan<byte> buffer)
            {
                if (buffer.IsEmpty) return null;
                RlpReader reader = new(buffer);
                return decoder.Decode(ref reader, RlpBehaviors.AllowExtraBytes | RlpBehaviors.TrustedInput);
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

        private static bool NotFound(out ValueHash256 blockHash, out UInt256 totalDifficulty)
        {
            blockHash = default;
            totalDifficulty = default;
            return false;
        }
    }
}
