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

using Nethermind.Config;

namespace Nethermind.Db.Rocks.Config
{
    [ConfigCategory(HiddenFromDocs = true)]
    public interface IDbConfig : IConfig
    {
        ulong WriteBufferSize { get; set; }
        uint WriteBufferNumber { get; set; }
        ulong BlockCacheSize { get; set; }
        bool CacheIndexAndFilterBlocks { get; set; }

        uint RecycleLogFileNum { get; set; }
        bool WriteAheadLogSync { get; set; }
        ulong ReceiptsDbWriteBufferSize { get; set; }
        uint ReceiptsDbWriteBufferNumber { get; set; }
        ulong ReceiptsDbBlockCacheSize { get; set; }
        bool ReceiptsDbCacheIndexAndFilterBlocks { get; set; }

        ulong BlocksDbWriteBufferSize { get; set; }
        uint BlocksDbWriteBufferNumber { get; set; }
        ulong BlocksDbBlockCacheSize { get; set; }
        bool BlocksDbCacheIndexAndFilterBlocks { get; set; }

        ulong HeadersDbWriteBufferSize { get; set; }
        uint HeadersDbWriteBufferNumber { get; set; }
        ulong HeadersDbBlockCacheSize { get; set; }
        bool HeadersDbCacheIndexAndFilterBlocks { get; set; }

        ulong BlockInfosDbWriteBufferSize { get; set; }
        uint BlockInfosDbWriteBufferNumber { get; set; }
        ulong BlockInfosDbBlockCacheSize { get; set; }
        bool BlockInfosDbCacheIndexAndFilterBlocks { get; set; }

        ulong PendingTxsDbWriteBufferSize { get; set; }
        uint PendingTxsDbWriteBufferNumber { get; set; }
        ulong PendingTxsDbBlockCacheSize { get; set; }
        bool PendingTxsDbCacheIndexAndFilterBlocks { get; set; }

        ulong CodeDbWriteBufferSize { get; set; }
        uint CodeDbWriteBufferNumber { get; set; }
        ulong CodeDbBlockCacheSize { get; set; }
        bool CodeDbCacheIndexAndFilterBlocks { get; set; }

        ulong BloomDbWriteBufferSize { get; set; }
        uint BloomDbWriteBufferNumber { get; set; }
        ulong BloomDbBlockCacheSize { get; set; }
        bool BloomDbCacheIndexAndFilterBlocks { get; set; }
        
        ulong WitnessDbWriteBufferSize { get; set; }
        uint WitnessDbWriteBufferNumber { get; set; }
        ulong WitnessDbBlockCacheSize { get; set; }
        bool WitnessDbCacheIndexAndFilterBlocks { get; set; }
        
        ulong CanonicalHashTrieDbWriteBufferSize { get; set; }
        uint CanonicalHashTrieDbWriteBufferNumber { get; set; }
        ulong CanonicalHashTrieDbBlockCacheSize { get; set; }
        bool CanonicalHashTrieDbCacheIndexAndFilterBlocks { get; set; }

        /// <summary>
        /// Enables DB Statistics - https://github.com/facebook/rocksdb/wiki/Statistics
        /// It can has a RocksDB perfomance hit between 5 and 10%.
        /// </summary>
        bool EnableDbStatistics { get; set; }
        bool EnableMetricsUpdater { get; set; }
        /// <summary>
        /// If not zero, dump rocksdb.stats to LOG every stats_dump_period_sec
        /// Default: 600 (10 min)
        /// </summary>
        uint StatsDumpPeriodSec { get; set; }
    }
}
