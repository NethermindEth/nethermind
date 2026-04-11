// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.ModuleTests
{
    public class XdcRocksDbConfigFactoryTests
    {
        [Test]
        public void CustomFactory_DoesNotThrow_And_Returns_ConfigForSnapshot()
        {
            IDbConfig dbConfig = new DbConfig();
            IPruningConfig pruning = Substitute.For<IPruningConfig>();
            IHardwareInfo hw = Substitute.For<IHardwareInfo>();
            ILogManager logManager = Substitute.For<ILogManager>();
            RocksDbConfigFactory baseFactory = new(dbConfig, pruning, hw, logManager, validateConfig: true);
            XdcRocksDbConfigFactory custom = new(baseFactory, dbConfig);

            IRocksDbConfig config = custom.GetForDatabase(XdcRocksDbConfigFactory.XdcSnapshotDbName, null);

            config.Should().NotBeNull();
            config.RocksDbOptions.Should().NotBeNull();
        }
    }
}
