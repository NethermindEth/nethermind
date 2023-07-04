// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Repositories
{
    public class ChainLevelInfoRepository : IChainLevelInfoRepository
    {
        private const int CacheSize = 64;

        private readonly object _writeLock = new();
        private readonly LruCache<long, ChainLevelInfo> _blockInfoCache = new LruCache<long, ChainLevelInfo>(CacheSize, CacheSize, "chain level infos");

        private readonly IDb _blockInfoDb;

        public ChainLevelInfoRepository(IDbProvider dbProvider)
            : this(dbProvider.BlockInfosDb)
        {
        }

        public ChainLevelInfoRepository(IDb blockInfoDb)
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
                LocalDelete();
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
                LocalPersistLevel();
            }
        }

        public BatchWrite StartBatch() => new(_writeLock);

        public ChainLevelInfo? LoadLevel(long number) => _blockInfoDb.Get(number, Rlp.GetStreamDecoder<ChainLevelInfo>(), _blockInfoCache);
    }
}
