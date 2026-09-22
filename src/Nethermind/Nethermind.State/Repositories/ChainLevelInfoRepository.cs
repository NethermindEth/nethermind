// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private const int CacheSize = 64;

        private readonly object _writeLock = new();
        private readonly object _deferredWriteLock = new();
        private readonly ConcurrentDictionary<ulong, PendingLevel> _pending = new();
        private readonly ClockCache<ulong, ChainLevelInfo> _blockInfoCache = new(CacheSize);
        private readonly IRlpDecoder<ChainLevelInfo> _decoder = Rlp.GetDecoder<ChainLevelInfo>()
            ?? throw new InvalidOperationException($"No RLP decoder is registered for {nameof(ChainLevelInfo)}.");

        private readonly IDb _blockInfoDb = blockInfoDb ?? throw new ArgumentNullException(nameof(blockInfoDb));

        private sealed class PendingLevel(ChainLevelInfo level, byte[] rlp)
        {
            public ChainLevelInfo Level { get; } = level;
            public byte[] Rlp { get; } = rlp;
        }

        /// <inheritdoc/>
        public void PersistLevelDeferred(ulong number, ChainLevelInfo level, Action<Action> enqueue, BatchWrite? batch = null)
        {
            if (batch is null || batch.Disposed)
            {
                PersistLevel(number, level, batch);
                return;
            }

            PendingLevel pending = new(level, _decoder.Encode(level).Bytes);
            _blockInfoCache.Set(number, level);
            _pending[number] = pending;
            enqueue(() =>
            {
                // Do not acquire the batch lock: a producer can hold it while the bounded writer backpressures.
                lock (_deferredWriteLock)
                {
                    if (_pending.TryGetValue(number, out PendingLevel? current) && ReferenceEquals(current, pending))
                    {
                        _blockInfoDb.PutSpan(number.ToBigEndianSpanWithoutLeadingZeros(out _), pending.Rlp);
                        _pending.TryRemove(new KeyValuePair<ulong, PendingLevel>(number, pending));
                    }
                }
            });
        }

        private void CancelPending(ulong number, bool persist = false)
        {
            lock (_deferredWriteLock)
            {
                // A canonical batch can still be open when the state barrier drains. Keep the suggested
                // level durable before removing its queued write in favor of that not-yet-committed batch.
                if (persist && _pending.TryGetValue(number, out PendingLevel? pending))
                    _blockInfoDb.PutSpan(number.ToBigEndianSpanWithoutLeadingZeros(out _), pending.Rlp);
                _pending.TryRemove(number, out _);
            }
        }

        public void Delete(ulong number, BatchWrite? batch = null)
        {
            void LocalDelete()
            {
                CancelPending(number);
                _blockInfoCache.Delete(number);
                _blockInfoDb.Delete(number);
            }

            if (batch is null || batch.Disposed)
            {
                lock (_writeLock)
                {
                    LocalDelete();
                }
            }
            else
            {
                CancelPending(number);
                _blockInfoCache.Delete(number);
                batch.WriteBatch.Delete(number);
            }
        }

        public void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null)
        {
            void LocalPersistLevel()
            {
                CancelPending(number, persist: true);
                _blockInfoCache.Set(number, level);
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
                CancelPending(number, persist: true);
                _blockInfoCache.Set(number, level);
                using ArrayPoolSpan<byte> rlp = _decoder.EncodeToArrayPoolSpan(level);
                batch.WriteBatch.PutSpan(number.ToBigEndianSpanWithoutLeadingZeros(out _), rlp);
            }
        }

        public BatchWrite StartBatch() => new(_writeLock, _blockInfoDb.StartWriteBatch);

        /// <inheritdoc/>
        public BatchWrite StartDeferredBatch() => new(_writeLock, () => new InMemoryWriteBatch(_blockInfoDb));

        public ChainLevelInfo? LoadLevel(ulong number) => _pending.TryGetValue(number, out PendingLevel? pending)
            ? pending.Level
            : _blockInfoDb.Get(number, Rlp.GetDecoder<ChainLevelInfo>(), _blockInfoCache);

        public IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers)
        {
            byte[][] keys = new byte[blockNumbers.Count][];
            using ArrayPoolListRef<ChainLevelInfo?> pendingLevels = new(blockNumbers.Count);
            for (int i = 0; i < blockNumbers.Count; i++)
            {
                keys[i] = blockNumbers[i].ToBigEndianByteArrayWithoutLeadingZeros();
                pendingLevels.Add(_pending.TryGetValue(blockNumbers[i], out PendingLevel? pending) ? pending.Level : null);
            }

            KeyValuePair<byte[], byte[]?>[] data = _blockInfoDb[keys];

            ArrayPoolList<ChainLevelInfo?> levels = new(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                if (pendingLevels[i] is { } pending)
                {
                    levels.Add(pending);
                }
                else
                {
                    byte[]? value = data[i].Value;
                    RlpReader reader = new(value);
                    levels.Add(value is null || value.Length == 0 ? null : _decoder.Decode(ref reader, RlpBehaviors.AllowExtraBytes));
                }
            }
            return levels;
        }

        void IClearableCache.ClearCache() => _blockInfoCache.Clear();
    }
}
