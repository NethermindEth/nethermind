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
            void ValidateDb<T>(params IDb[] dbs) where T : IDb
            {
                foreach (IDb db in dbs)
                {
                    db.Should().BeAssignableTo<T>(db.Name);
                }
            }

            IJsonSerializer jsonSerializer = Substitute.For<IJsonSerializer>();
            IJsonRpcClient jsonRpcClient = Substitute.For<IJsonRpcClient>();
            IMemDbFactory rpcDbFactory = new RpcDbFactory(new MemDbFactory(), null, jsonSerializer, jsonRpcClient, LimboLogs.Instance);

            IDbProvider memDbProvider = new DbProvider(DbModeHint.Mem);
            StandardDbInitializer standardDbInitializer = new(memDbProvider, null, rpcDbFactory, Substitute.For<IFileSystem>());
            standardDbInitializer.InitStandardDbs(true);

            ValidateDb<ReadOnlyDb>(
                memDbProvider.BlocksDb,
                memDbProvider.BloomDb,
                memDbProvider.HeadersDb,
                memDbProvider.ReceiptsDb,
                memDbProvider.BlockInfosDb);

            ValidateDb<ReadOnlyDb>(
                memDbProvider.CodeDb);

            ValidateDb<FullPruningDb>(
                memDbProvider.StateDb);
        }
    }
}
