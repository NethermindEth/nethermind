// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.Self)]
    public class DiscoveryPersistenceManagerTests
    {
        private MemDb _discoveryDb = null!;
        private INetworkStorage _networkStorage = null!;
        private IDiscoveryConfig _discoveryConfig = null!;
        private ILogManager _logManager = null!;
        private DiscoveryPersistenceManager _persistenceManager = null!;
        private IDiscoveryManager _discoveryManager;

        [SetUp]
        public void Setup()
        {
            NetworkNodeDecoder.Init();

            _discoveryDb = new MemDb();
            _networkStorage = new NetworkStorage(_discoveryDb, LimboLogs.Instance);
            _discoveryConfig = new DiscoveryConfig()
            {
                DiscoveryPersistenceInterval = 100,
            };
            _logManager = LimboLogs.Instance;
            _discoveryManager = Substitute.For<IDiscoveryManager>();

            _persistenceManager = new DiscoveryPersistenceManager(
                _networkStorage,
                _discoveryManager,
                _discoveryConfig,
                _logManager);
        }

        [TearDown]
        public void Teardown()
        {
            _discoveryDb.Dispose();
        }

        [Test]
        public async Task RunDiscoveryPersistenceCommit_Should_Update_Nodes_In_Storage()
        {
            var nodes = new[]
            {
                new Node(TestItem.PublicKeyA, "192.168.1.1", 30303),
                new Node(TestItem.PublicKeyB, "192.168.1.2", 30303)
            };

            var cls = new CancellationTokenSource().ThatCancelAfter(TimeSpan.FromMilliseconds(5000));

            var lifecycleManagers = nodes.Select((node) =>
            {
                INodeLifecycleManager lifecycle = Substitute.For<INodeLifecycleManager>();
                lifecycle.ManagedNode.Returns(node);
                return lifecycle;
            }).ToArray();

            _discoveryManager.GetNodeLifecycleManagers().Returns(lifecycleManagers);

            _ = _persistenceManager.RunDiscoveryPersistenceCommit(cls.Token);

            // Wait a bit to allow at least one persistence cycle to complete
            await Task.Delay(_discoveryConfig.DiscoveryPersistenceInterval * 2, cls.Token);

            await cls.CancelAsync();

            _discoveryDb.Count.Should().Be(2);
        }
    }
}
