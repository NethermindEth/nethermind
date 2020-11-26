using System;

namespace Nethermind.Db
{
    public class RocksDbSettings
    {
        public string DbName { get; set; }

        public string DbPath { get; set; }

        public Action UpdateReadMetrics { get; set; }

        public Action UpdateWriteMetrics { get; set; }

        public uint DbSize { get; set; }
    }
    public interface IRocksDbFactory
    {
        IDb CreateDb(RocksDbSettings rocksDbSettings);

        ISnapshotableDb CreateSnapshotableDb(RocksDbSettings rocksDbSettings);
    }
}
