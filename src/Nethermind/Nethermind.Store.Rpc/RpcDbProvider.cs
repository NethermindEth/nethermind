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
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Store.Rpc
{
    public class RpcDbProvider : IDbProvider
    {
        private readonly IDbProvider _recordDbProvider;

        public RpcDbProvider(IJsonSerializer serializer, IJsonRpcClient client, ILogManager logManager, IDbProvider recordDbProvider)
        {
            _recordDbProvider = recordDbProvider;
            StateDb = new StateDb(new ReadOnlyDb(new RpcDb(DbNames.State, serializer, client, logManager, recordDbProvider?.StateDb)));
            CodeDb = new StateDb(new ReadOnlyDb(new RpcDb(DbNames.Code, serializer, client, logManager, recordDbProvider?.CodeDb)));
            ReceiptsDb = new StateDb(new ReadOnlyDb(new RpcDb(DbNames.Receipts, serializer, client, logManager, recordDbProvider?.ReceiptsDb)));
            BlocksDb = new StateDb(new ReadOnlyDb(new RpcDb(DbNames.Blocks, serializer, client, logManager, recordDbProvider?.BlocksDb)));
            BlockInfosDb = new StateDb(new ReadOnlyDb(new RpcDb(DbNames.BlockInfos, serializer, client, logManager, recordDbProvider?.BlockInfosDb)));
        }
        
        public ISnapshotableDb StateDb { get; }
        public ISnapshotableDb CodeDb { get; }        
        public IDb ReceiptsDb { get; }
        public IDb BlocksDb { get; }
        public IDb BlockInfosDb { get; }

        public void Dispose()
        {
            StateDb?.Dispose();
            CodeDb?.Dispose();
            ReceiptsDb?.Dispose();
            BlocksDb?.Dispose();
            BlockInfosDb?.Dispose();
            _recordDbProvider?.Dispose();
        }
    }
}