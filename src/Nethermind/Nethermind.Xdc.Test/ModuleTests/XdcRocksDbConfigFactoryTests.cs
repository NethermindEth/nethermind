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
        private const string SnapshotDbName = "XdcSnapshots";

        [Test]
        public void BaseFactory_Throws_When_ConfigMissingForSnapshot()
        {
            // Arrange
            IDbConfig dbConfig = new DbConfig();
            var pruning = Substitute.For<IPruningConfig>();
            var hw = Substitute.For<IHardwareInfo>();
            var logManager = Substitute.For<ILogManager>();
            // construct base factory with validation enabled
            var baseFactory = new RocksDbConfigFactory(dbConfig, pruning, hw, logManager, validateConfig: true);

            // Act
            Action act = () => baseFactory.GetForDatabase(SnapshotDbName, null);

            // Assert
            act.Should().Throw<InvalidConfigurationException>();
        }

        [Test]
        public void CustomFactory_DoesNotThrow_And_Returns_ConfigForSnapshot()
        {
            // Arrange
            IDbConfig dbConfig = new DbConfig();
            var pruning = Substitute.For<IPruningConfig>();
            var hw = Substitute.For<IHardwareInfo>();
            var logManager = Substitute.For<ILogManager>();
            var baseFactory = new RocksDbConfigFactory(dbConfig, pruning, hw, logManager, validateConfig: true);
            var custom = new XdcRocksDbConfigFactory(baseFactory, dbConfig);

            // Act
            IRocksDbConfig config = custom.GetForDatabase(SnapshotDbName, null);

            // Assert
            config.Should().NotBeNull();
            config.RocksDbOptions.Should().NotBeNull();
        }
    }
}
