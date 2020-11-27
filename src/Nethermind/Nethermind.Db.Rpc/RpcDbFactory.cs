using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Db.Rocks;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Db.Rpc
{
    public class RpcDbFactory : IRocksDbFactory
    {
        private readonly IRocksDbFactory _wrappedRocksDbFactory;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IJsonRpcClient _jsonRpcClient;
        private readonly ILogManager _logManager;


        public IDb CreateDb(RocksDbSettings rocksDbSettings)
        {
            // ToDo -> base db path change
            var rocksDb = _wrappedRocksDbFactory.CreateDb(rocksDbSettings);
            return new ReadOnlyDb(new RpcDb(rocksDb.Name, _jsonSerializer, _jsonRpcClient, _logManager, rocksDb), true);
        }

        public ISnapshotableDb CreateSnapshotableDb(RocksDbSettings rocksDbSettings)
        {
            return new StateDb(CreateDb(rocksDbSettings));
        }
    }
}
