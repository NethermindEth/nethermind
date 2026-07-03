// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
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
            static void ValidateDb<T>(params object[] dbs)
            {
                foreach (object db in dbs)
                {
                    Assert.That(db, Is.AssignableTo<T>());
                }
            }

            IJsonSerializer jsonSerializer = Substitute.For<IJsonSerializer>();
            IJsonRpcClient jsonRpcClient = Substitute.For<IJsonRpcClient>();
            IDbFactory rpcDbFactory = new RpcDbFactory(new MemDbFactory(), jsonSerializer, jsonRpcClient, LimboLogs.Instance);

            using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddSingleton<IDbFactory>(rpcDbFactory)
                .Build();

            IDbProvider memDbProvider = container.Resolve<IDbProvider>();

            ValidateDb<ReadOnlyColumnsDb<ReceiptsColumns>>(
                memDbProvider.ReceiptsDb);

            // Block-data dbs sit behind the write-behind decorator; the rpc read-only db is inside.
            foreach (IDb db in new IDb[] { memDbProvider.BlocksDb, memDbProvider.HeadersDb, memDbProvider.BlockInfosDb })
            {
                Assert.That(db, Is.TypeOf<WriteBehindDb>());
                Assert.That(((WriteBehindDb)db).Inner, Is.AssignableTo<ReadOnlyDb>());
            }

            ValidateDb<ReadOnlyDb>(
                memDbProvider.CodeDb);

            ValidateDb<FullPruningDb>(
                memDbProvider.StateDb);
        }
    }
}
