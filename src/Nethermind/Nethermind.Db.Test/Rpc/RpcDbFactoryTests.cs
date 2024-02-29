// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using FluentAssertions;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rpc;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test.Rpc
{
    [Parallelizable(ParallelScope.All)]
    public class RpcDbFactoryTests
    {
        [Test]
        public void ValidateDbs()
        {
            void ValidateDb<T>(params object[] dbs)
            {
                foreach (object db in dbs)
                {
                    db.Should().BeAssignableTo<T>();
                }
            }

            IJsonSerializer jsonSerializer = Substitute.For<IJsonSerializer>();
            IJsonRpcClient jsonRpcClient = Substitute.For<IJsonRpcClient>();
            IDbFactory rpcDbFactory = new RpcDbFactory(new MemDbFactory(), jsonSerializer, jsonRpcClient, LimboLogs.Instance);

            IDbProvider memDbProvider = new DbProvider();
            StandardDbInitializer standardDbInitializer = new(memDbProvider, rpcDbFactory, Substitute.For<IFileSystem>());
            standardDbInitializer.InitStandardDbs(true);

            ValidateDb<ReadOnlyColumnsDb<ReceiptsColumns>>(
                memDbProvider.ReceiptsDb);

            ValidateDb<ReadOnlyDb>(
                memDbProvider.BlocksDb,
                memDbProvider.BloomDb,
                memDbProvider.HeadersDb,
                memDbProvider.BlockInfosDb);

            ValidateDb<ReadOnlyDb>(
                memDbProvider.CodeDb);

            ValidateDb<FullPruningDb>(
                memDbProvider.StateDb);
        }
    }
}
