//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
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
