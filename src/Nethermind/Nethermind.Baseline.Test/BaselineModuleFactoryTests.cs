// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Baseline.Database;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [TestFixture]
    public class BaselineModuleFactoryTests
    {
        [Test]
        public void Can_create_many()
        {
            var dbProvider = new DbProvider(DbModeHint.Mem);
            dbProvider.RegisterDb(BaselineDbNames.BaselineTree, new MemDb());
            dbProvider.RegisterDb(BaselineDbNames.BaselineTreeMetadata, new MemDb());
            BaselineModuleFactory factory = new BaselineModuleFactory(
                Substitute.For<ITxSender>(),
                Substitute.For<IStateReader>(),
                Substitute.For<ILogFinder>(),
                Substitute.For<IBlockFinder>(),
                new AbiEncoder(),
                Substitute.For<IFileSystem>(),
                LimboLogs.Instance,
                Substitute.For<IBlockProcessor>(),
                new DisposableStack(),
                dbProvider);

            var a = factory.Create();
            var b = factory.Create();

            a.Should().NotBe(b);
        }
    }
}
