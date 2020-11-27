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
using System.Collections.Generic;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Db.Rpc
{
    public class RpcDbProvider : IDbProvider
    {
        private readonly IDbProvider _recordDbProvider;
        private readonly IJsonSerializer _serializer;
        private readonly IJsonRpcClient _client;
        private readonly ILogManager _logManager;
        private List<IDb> _otherDbs = new List<IDb>();

        public RpcDbProvider(IJsonSerializer serializer, IJsonRpcClient client, ILogManager logManager, IDbProvider recordDbProvider)
        {
            _recordDbProvider = recordDbProvider;
            _serializer = serializer;
            _client = client;
            _logManager = logManager;
            StateDb = new StateDb(new ReadOnlyDb(new RpcDb(DbNames.State, serializer, client, logManager, recordDbProvider?.StateDb), true));
            CodeDb = new StateDb(new ReadOnlyDb(new RpcDb(DbNames.Code, serializer, client, logManager, recordDbProvider?.CodeDb), true));
            ReceiptsDb = new ReadOnlyColumnsDb<ReceiptsColumns>(new RpcColumnsDb<ReceiptsColumns>(DbNames.Receipts, serializer, client, logManager, recordDbProvider?.ReceiptsDb), true);
            BlocksDb = new ReadOnlyDb(new RpcDb(DbNames.Blocks, serializer, client, logManager, recordDbProvider?.BlocksDb), true);
            HeadersDb = new ReadOnlyDb(new RpcDb(DbNames.Headers, serializer, client, logManager, recordDbProvider?.HeadersDb), true);
            BlockInfosDb = new ReadOnlyDb(new RpcDb(DbNames.BlockInfos, serializer, client, logManager, recordDbProvider?.BlockInfosDb), true);
            PendingTxsDb = new ReadOnlyDb(new RpcDb(DbNames.PendingTxs, serializer, client, logManager, recordDbProvider?.ReceiptsDb), true);
            BloomDb = new ReadOnlyDb(new RpcDb(DbNames.Bloom, serializer, client, logManager, recordDbProvider?.BloomDb), true);
        }
        
        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }        
        public IColumnsDb<ReceiptsColumns> ReceiptsDb { get; }
        public IDb BlocksDb { get; }
        public IDb HeadersDb { get; }
        public IDb BlockInfosDb { get; }
        public IDb PendingTxsDb { get; }
        public IDb BloomDb { get; }
        public IDb ChtDb { get; }
        public IDb BeamStateDb { get; } = new MemDb();

        public IEnumerable<IDb> OtherDbs => _otherDbs;

        public DbModeHint DbMode => throw new NotImplementedException();

        public void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            HeadersDb?.Dispose();
            BlockInfosDb?.Dispose();
            PendingTxsDb?.Dispose();
            _recordDbProvider?.Dispose();
            BloomDb?.Dispose();
            ChtDb?.Dispose();

            if (_otherDbs != null)
            {
                foreach (var otherDb in _otherDbs)
                {
                    otherDb?.Dispose();
                }
            }
        }

        public T GetDb<T>(string dbName) where T : IDb
        {
            throw new NotImplementedException();
        }

        public void RegisterDb<T>(string dbName, T db) where T : IDb
        {
            throw new NotImplementedException();
        }
    }
}
