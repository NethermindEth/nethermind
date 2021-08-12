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
// 

using System;

namespace Nethermind.Db
{
    public class RocksDbSettings
    {
        public RocksDbSettings(string name, string path)
        {
            DbName = name;
            DbPath = path;
        }
        
        public string DbName { get; }
        public string DbPath { get; }

        public Action? UpdateReadMetrics { get; init; }
        public Action? UpdateWriteMetrics { get; init; }

        public ulong? WriteBufferSize { get; init; }
        public uint? WriteBufferNumber { get; init; }
        public ulong? BlockCacheSize { get; init; }
        public bool? CacheIndexAndFilterBlocks { get; init; }
    }
}
