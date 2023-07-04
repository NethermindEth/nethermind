// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public string DbName { get; private set; }
        public string DbPath { get; private set; }

        public Action? UpdateReadMetrics { get; init; }
        public Action? UpdateWriteMetrics { get; init; }

        public ulong? WriteBufferSize { get; init; }
        public uint? WriteBufferNumber { get; init; }
        public ulong? BlockCacheSize { get; init; }
        public bool? CacheIndexAndFilterBlocks { get; init; }

        public bool DeleteOnStart { get; set; }
        public bool CanDeleteFolder { get; set; } = true;

        public RocksDbSettings Clone(string name, string path)
        {
            RocksDbSettings settings = (RocksDbSettings)MemberwiseClone();
            settings.DbName = name;
            settings.DbPath = path;
            return settings;
        }

        public RocksDbSettings Clone() => (RocksDbSettings)MemberwiseClone();

        public override string ToString() => $"{DbName}:{DbPath}";
    }
}
