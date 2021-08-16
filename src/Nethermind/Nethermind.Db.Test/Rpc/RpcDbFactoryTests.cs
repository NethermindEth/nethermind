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

using FluentAssertions;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test.Rpc
{
    public class RpcDbFactoryTests
    {
        [Test]
        public void ValidateDbs()
        {
            void ValidateDb<T>(params IDb[] dbs) where T : IDb
            {
                foreach (IDb db in dbs)
                {
                    db.Should().BeAssignableTo<T>(db.Name);
                    db.Innermost.Should().BeAssignableTo<RpcDb>(db.Name);
                }
            }
            
            IJsonSerializer jsonSerializer = Substitute.For<IJsonSerializer>();
            IJsonRpcClient jsonRpcClient = Substitute.For<IJsonRpcClient>();
            IMemDbFactory rpcDbFactory = new RpcDbFactory(new MemDbFactory(), null, jsonSerializer, jsonRpcClient, LimboLogs.Instance);

            IDbProvider memDbProvider = new DbProvider(DbModeHint.Mem);
            StandardDbInitializer standardDbInitializer = new(memDbProvider, null, rpcDbFactory);
            standardDbInitializer.InitStandardDbs(true);

            ValidateDb<ReadOnlyDb>(
                memDbProvider.BlocksDb,
                memDbProvider.BloomDb,
                memDbProvider.HeadersDb,
                memDbProvider.ReceiptsDb,
                memDbProvider.BlockInfosDb);

            ValidateDb<ReadOnlyDb>(
                memDbProvider.StateDb,
                memDbProvider.CodeDb);
        }
    }
}
