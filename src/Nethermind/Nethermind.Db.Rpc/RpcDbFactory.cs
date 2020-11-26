using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Db.Rocks;

namespace Nethermind.Db.Rpc
{
    public class RpcDbFactory : IRocksDbFactory
    {

        public IDb CreateDb(RocksDbSettings rocksDbSettings)
        {
            throw new NotImplementedException();
        }

        public ISnapshotableDb CreateSnapshotableDb(RocksDbSettings rocksDbSettings)
        {
            throw new NotImplementedException();
        }
    }
}
