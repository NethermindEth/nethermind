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

namespace Nethermind.Db
{
    public class RocksDbSettings
    {
        public string DbName { get; set; }

        public string DbPath { get; set; }

        public Action UpdateReadMetrics { get; set; }
        public Action UpdateWriteMetrics { get; set; }

        public ulong? WriteBufferSize { get; set; }
        public uint? WriteBufferNumber { get; set; }
        public ulong? BlockCacheSize { get; set; }
        public bool? CacheIndexAndFilterBlocks { get; set; }
    }

    public interface IRocksDbFactory
    {
        IDb CreateDb(RocksDbSettings rocksDbSettings);

        ISnapshotableDb CreateSnapshotableDb(RocksDbSettings rocksDbSettings);

        ISnapshotableDb CreateSnapshotableMemoryMappedDb(RocksDbSettings rocksDbSettings);

        IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings);
    }
}
