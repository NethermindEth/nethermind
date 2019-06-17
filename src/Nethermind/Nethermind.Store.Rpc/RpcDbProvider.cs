/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;

namespace Nethermind.Store.Rpc
{
    public class RpcDbProvider : IDbProvider
    {
        private readonly IDbProvider _recordDbProvider;

        public RpcDbProvider(IJsonSerializer serializer, IJsonRpcClient client, ILogManager logManager, IDbProvider recordDbProvider)
        {
            _recordDbProvider = recordDbProvider;
            StateDb = new StateDb(new ReadOnlyDb(new RpcDb(DbNames.State, serializer, client, logManager, recordDbProvider?.StateDb), true));
            CodeDb = new StateDb(new ReadOnlyDb(new RpcDb(DbNames.Code, serializer, client, logManager, recordDbProvider?.CodeDb), true));
            ReceiptsDb = new ReadOnlyDb(new RpcDb(DbNames.Receipts, serializer, client, logManager, recordDbProvider?.ReceiptsDb), true);
            BlocksDb = new ReadOnlyDb(new RpcDb(DbNames.Blocks, serializer, client, logManager, recordDbProvider?.BlocksDb), true);
            HeadersDb = new ReadOnlyDb(new RpcDb(DbNames.Headers, serializer, client, logManager, recordDbProvider?.HeadersDb), true);
            BlockInfosDb = new ReadOnlyDb(new RpcDb(DbNames.BlockInfos, serializer, client, logManager, recordDbProvider?.BlockInfosDb), true);
            PendingTxsDb = new ReadOnlyDb(new RpcDb(DbNames.PendingTxs, serializer, client, logManager, recordDbProvider?.ReceiptsDb), true);
            TraceDb = new ReadOnlyDb(new RpcDb(DbNames.Trace, serializer, client, logManager, recordDbProvider?.ReceiptsDb), true);
            ConfigsDb = new ReadOnlyDb(new RpcDb(DbNames.Configs, serializer, client, logManager, recordDbProvider?.ConfigsDb), true);
            EthRequestsDb = new ReadOnlyDb(new RpcDb(DbNames.EthRequests, serializer, client, logManager, recordDbProvider?.EthRequestsDb), true); 
        }
        
        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }        
        public IDb ReceiptsDb { get; }
        public IDb BlocksDb { get; }
        public IDb HeadersDb { get; }
        public IDb BlockInfosDb { get; }
        public IDb PendingTxsDb { get; }
        public IDb TraceDb { get; }
        public IDb ConfigsDb { get; }
        public IDb EthRequestsDb { get; }

        public void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            HeadersDb?.Dispose();
            BlockInfosDb?.Dispose();
            PendingTxsDb?.Dispose();
            ConfigsDb?.Dispose();
            EthRequestsDb?.Dispose();
            _recordDbProvider?.Dispose();
        }
    }
}