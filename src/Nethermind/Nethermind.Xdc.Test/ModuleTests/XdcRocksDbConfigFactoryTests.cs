// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Xdc;
using NSubstitute;
using NUnit.Framework;
using System;

namespace Nethermind.Xdc.Test.ModuleTests
{
    public class XdcRocksDbConfigFactoryTests
    {
        [Test]
        public void CustomFactory_DoesNotThrow_And_Returns_ConfigForSnapshot()
        {
            IDbConfig dbConfig = new DbConfig();
            var pruning = Substitute.For<IPruningConfig>();
            var hw = Substitute.For<IHardwareInfo>();
            var logManager = Substitute.For<ILogManager>();
            var baseFactory = new RocksDbConfigFactory(dbConfig, pruning, hw, logManager, validateConfig: true);
            var custom = new XdcRocksDbConfigFactory(baseFactory, dbConfig);

            IRocksDbConfig config = custom.GetForDatabase(XdcRocksDbConfigFactory.XdcSnapshotDbName, null);

            config.Should().NotBeNull();
            config.RocksDbOptions.Should().NotBeNull();
        }
    }
}
