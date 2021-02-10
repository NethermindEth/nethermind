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
        private readonly ICache<long, ChainLevelInfo> _blockInfoCache = new LruCache<long, ChainLevelInfo>(CacheSize, CacheSize, "chain level infos");

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
