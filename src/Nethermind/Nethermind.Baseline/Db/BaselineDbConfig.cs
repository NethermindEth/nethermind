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
// 

using System;
using Nethermind.Baseline.Config;
using Nethermind.Db.Rocks.Config;

namespace Nethermind.Baseline.Db
{
    public class BaselineDbConfig : IBaselineDbConfig
    {
        private readonly IBaselineConfig _baselineConfig;
        private readonly IDbConfig _dbConfig;
        public BaselineDbConfig(
            IBaselineConfig baselineConfig,
            IDbConfig dbConfig)
        {
            _baselineConfig = baselineConfig ?? throw new ArgumentNullException(nameof(baselineConfig));
            _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
        }
        public bool BaselineTreeDbCacheIndexAndFilterBlocks => _baselineConfig.BaselineTreeDbCacheIndexAndFilterBlocks;
        public ulong BaselineTreeDbBlockCacheSize => _baselineConfig.BaselineTreeDbBlockCacheSize;
        public ulong BaselineTreeDbWriteBufferSize => _baselineConfig.BaselineTreeDbWriteBufferSize;
        public uint BaselineTreeDbWriteBufferNumber => _baselineConfig.BaselineTreeDbWriteBufferNumber;

        public bool BaselineTreeMetadataDbCacheIndexAndFilterBlocks => _baselineConfig.BaselineTreeMetadataDbCacheIndexAndFilterBlocks;
        public ulong BaselineTreeMetadataDbBlockCacheSize => _baselineConfig.BaselineTreeMetadataDbBlockCacheSize;
        public ulong BaselineTreeMetadataDbWriteBufferSize => _baselineConfig.BaselineTreeMetadataDbWriteBufferSize;
        public uint BaselineTreeMetadataDbWriteBufferNumber => _baselineConfig.BaselineTreeMetadataDbWriteBufferNumber;

        public ulong WriteBufferSize 
        {
            get => _dbConfig.WriteBufferSize;
            set => _dbConfig.WriteBufferSize = value; 
        }
        public uint WriteBufferNumber 
        { 
            get => _dbConfig.WriteBufferNumber;
            set => _dbConfig.WriteBufferNumber = value;
        }
        public ulong BlockCacheSize
        {
            get => _dbConfig.BlockCacheSize;
            set => _dbConfig.BlockCacheSize = value;
        }
        public bool CacheIndexAndFilterBlocks
        {
            get => _dbConfig.CacheIndexAndFilterBlocks;
            set => _dbConfig.CacheIndexAndFilterBlocks = value;
        }
        public uint RecycleLogFileNum
        {
            get => _dbConfig.RecycleLogFileNum;
            set => _dbConfig.RecycleLogFileNum = value;
        }
        public bool WriteAheadLogSync
        {
            get => _dbConfig.WriteAheadLogSync;
            set => _dbConfig.WriteAheadLogSync = value;
        }
    }
}
