// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Repositories
{
    public class ChainLevelInfoRepository : IChainLevelInfoRepository
    {
        private const int CacheSize = 64;

        private readonly object _writeLock = new();
        private readonly ClockCache<long, ChainLevelInfo> _blockInfoCache = new(CacheSize);
        private readonly IRlpValueDecoder<ChainLevelInfo> _decoder = Rlp.GetValueDecoder<ChainLevelInfo>();

        private readonly IDb _blockInfoDb;

        public ChainLevelInfoRepository([KeyFilter(DbNames.BlockInfos)] IDb blockInfoDb)
        {
            _blockInfoDb = blockInfoDb ?? throw new ArgumentNullException(nameof(blockInfoDb));
        }

        public void Delete(long number, BatchWrite? batch = null)
        {
            void LocalDelete()
            {
                _blockInfoCache.Delete(number);
                _blockInfoDb.Delete(number);
            }

            bool needLock = batch?.Disposed != false;
            if (needLock)
            {
                lock (_writeLock)
                {
                    LocalDelete();
                }
            }
            else
            {
                _blockInfoCache.Delete(number);
                batch.WriteBatch.Delete(number);
            }
        }

        public void PersistLevel(long number, ChainLevelInfo level, BatchWrite? batch = null)
        {
            void LocalPersistLevel()
            {
                _blockInfoCache.Set(number, level);
                _blockInfoDb.Set(number, Rlp.Encode(level).Bytes);
            }

            bool needLock = batch?.Disposed != false;
            if (needLock)
            {
                lock (_writeLock)
                {
                    LocalPersistLevel();
                }
            }
            else
            {
                _blockInfoCache.Set(number, level);
                batch.WriteBatch.Set(number, Rlp.Encode(level).Bytes);
            }
        }

        public BatchWrite StartBatch() => new(_writeLock, _blockInfoDb.StartWriteBatch());

        public ChainLevelInfo? LoadLevel(long number) => _blockInfoDb.Get(number, Rlp.GetStreamDecoder<ChainLevelInfo>(), _blockInfoCache);

        public IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(IReadOnlyList<long> blockNumbers)
        {
            byte[][] keys = new byte[blockNumbers.Count][];
            for (var i = 0; i < blockNumbers.Count; i++)
            {
                keys[i] = blockNumbers[i].ToBigEndianByteArrayWithoutLeadingZeros();
            }

            KeyValuePair<byte[], byte[]?>[] data = _blockInfoDb[keys];

            return data.Select((kv) =>
                {
                    if (kv.Value == null || kv.Value.Length == 0) return null;
                    var rlpValueContext = kv.Value.AsRlpValueContext();
                    return _decoder.Decode(ref rlpValueContext, RlpBehaviors.AllowExtraBytes);
                })
                .ToPooledList(data.Length);
        }
    }
}
