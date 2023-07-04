// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.Network.StaticNodes;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class StaticNodesManagerTests
    {
        private IStaticNodesManager _staticNodesManager;

        private const string Enode =
            "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09@192.81.208.223:30303";

        [SetUp]
        public void Setup()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "test-static-nodes.json");
            var logManager = LimboLogs.Instance;
            _staticNodesManager = new StaticNodesManager(path, logManager);
        }

        [Test]
        public async Task init_should_load_static_nodes_from_the_file()
        {
            await _staticNodesManager.InitAsync();
            _staticNodesManager.Nodes.Count().Should().Be(2);
        }

        [Test]
        public async Task add_should_save_a_new_static_node_and_trigger_an_event()
        {
            var eventRaised = false;
            _staticNodesManager.NodeAdded += (s, e) => { eventRaised = true; };
            _staticNodesManager.Nodes.Count().Should().Be(0);
            await _staticNodesManager.AddAsync(Enode, false);
            _staticNodesManager.Nodes.Count().Should().Be(1);
            eventRaised.Should().BeTrue();
        }

        [Test]
        public async Task is_static_should_report_correctly()
        {
            _staticNodesManager.IsStatic(Enode).Should().BeFalse();
            await _staticNodesManager.AddAsync(Enode, false);
            _staticNodesManager.IsStatic(Enode).Should().BeTrue();
        }

        [Test]
        public async Task remove_should_delete_an_existing_static_node_and_trigger_an_event()
        {
            var eventRaised = false;
            _staticNodesManager.NodeRemoved += (s, e) => { eventRaised = true; };
            await _staticNodesManager.AddAsync(Enode, false);
            _staticNodesManager.Nodes.Count().Should().Be(1);
            await _staticNodesManager.RemoveAsync(Enode, false);
            _staticNodesManager.Nodes.Count().Should().Be(0);
            eventRaised.Should().BeTrue();
        }

        [Test]
        public async Task init_should_load_static_nodes_from_empty_file()
        {
            using var tempFile = TempPath.GetTempFile();
            await File.WriteAllTextAsync(tempFile.Path, string.Empty);
            _staticNodesManager = new StaticNodesManager(tempFile.Path, LimboLogs.Instance);
            await _staticNodesManager.InitAsync();
            _staticNodesManager.Nodes.Count().Should().Be(0);
        }
    }
}
