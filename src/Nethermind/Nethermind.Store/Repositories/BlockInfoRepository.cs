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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Encoding;

namespace Nethermind.Store.Repositories
{
    public class BlockInfoRepository : IBlockInfoRepository
    {
        private const int CacheSize = 64;
        
        private ReaderWriterLockSlim _blockInfoLock = new ReaderWriterLockSlim();
        private readonly LruCache<long, ChainLevelInfo> _blockInfoCache = new LruCache<long, ChainLevelInfo>(CacheSize);
        
        private readonly IDb _blockInfoDb;
        
        public BlockInfoRepository(IDb blockInfoDb)
        {
            _blockInfoDb = blockInfoDb ?? throw new ArgumentNullException(nameof(blockInfoDb));
        }

        public void Delete(long number, BatchWrite batch = null)
        {
            Batch(batch, () =>
            {
                _blockInfoCache.Delete(number);
                _blockInfoDb.Delete(number);
            });
        }

        public void PersistLevel(long number, ChainLevelInfo level, BatchWrite batch = null)
        {
            Batch(batch, () =>
            {
                // _blockInfoCache.Set(number, level);
                _blockInfoDb.Set(number, Rlp.Encode(level).Bytes);
            });
        }

        public BatchWrite StartBatch() => new BatchWrite(_blockInfoLock);

        public ChainLevelInfo LoadLevel(long number)
        {
            ChainLevelInfo chainLevelInfo = _blockInfoCache.Get(number);
            
            if (chainLevelInfo == null)
            {
                byte[] levelBytes = _blockInfoDb.Get(number);
                if (levelBytes != null)
                {
                    chainLevelInfo = Rlp.Decode<ChainLevelInfo>(new Rlp(levelBytes));
                }
            }

            return chainLevelInfo;
        }

        private void Batch(BatchWrite batch, Action action)
        {
            bool needLock = batch?.Disposed != false;
            if (needLock)
            {
                try
                {
                    _blockInfoLock.EnterWriteLock();
                    action();
                }
                finally
                {
                    _blockInfoLock.ExitWriteLock();
                }
            }
            else
            {
                action();
            }
        }
    }
}