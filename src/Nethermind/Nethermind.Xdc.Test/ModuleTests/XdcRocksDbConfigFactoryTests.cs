// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db.Rocks.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests
{
    public class XdcRocksDbConfigFactoryTests
    {
        [TestCase(XdcRocksDbConfigFactory.XdcSnapshotDbName)]
        [TestCase(XdcRocksDbConfigFactory.XdcRewardsDbName)]
        public void GetForDatabase_ForXdcDatabases_BuildsConfigWithoutTheBaseFactory(string dbName)
        {
            IRocksDbConfigFactory baseFactory = Substitute.For<IRocksDbConfigFactory>();
            DbConfig dbConfig = new();
            XdcRocksDbConfigFactory custom = new(baseFactory, dbConfig);

            IRocksDbConfig config = custom.GetForDatabase(dbName, null);

            Assert.That(config, Is.InstanceOf<PerTableDbConfig>());
            // The Xdc databases have no dedicated options in IDbConfig, so the config must
            // fall back to the unprefixed options. A wrong database name appends prefixed options.
            Assert.That(config.RocksDbOptions, Is.EqualTo(dbConfig.RocksDbOptions));
            baseFactory.DidNotReceive().GetForDatabase(Arg.Any<string>(), Arg.Any<string?>());
        }

        [Test]
        public void GetForDatabase_ForOtherDatabases_DelegatesToTheBaseFactory()
        {
            IRocksDbConfigFactory baseFactory = Substitute.For<IRocksDbConfigFactory>();
            IRocksDbConfig baseConfig = Substitute.For<IRocksDbConfig>();
            baseFactory.GetForDatabase("State", "Code").Returns(baseConfig);
            XdcRocksDbConfigFactory custom = new(baseFactory, new DbConfig());

            Assert.That(custom.GetForDatabase("State", "Code"), Is.SameAs(baseConfig));
        }
    }
}
