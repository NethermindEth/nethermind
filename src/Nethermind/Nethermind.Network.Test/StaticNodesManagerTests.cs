// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.Network.StaticNodes;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class StaticNodesManagerTests
    {
        private IStaticNodesManager _staticNodesManager;

        private const string EnodeString =
            "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09@192.81.208.223:30303";

        private static readonly NetworkNode Enode = new(EnodeString);

        [SetUp]
        public void Setup()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "test-static-nodes.json");
            LimboLogs logManager = LimboLogs.Instance;
            _staticNodesManager = new StaticNodesManager(path, logManager);
        }

        [Test]
        public async Task init_should_load_static_nodes_from_the_file()
        {
            await _staticNodesManager.InitAsync();
            Assert.That(_staticNodesManager.Nodes.Count(), Is.EqualTo(2));
        }

        [Test]
        public async Task add_should_save_a_new_static_node_and_pass_it_to_output()
        {
            ValueTask<List<Node>> listTask = _staticNodesManager.DiscoverNodes(default).Take(1).ToListAsync();

            Assert.That(_staticNodesManager.Nodes.Count(), Is.EqualTo(0));
            await _staticNodesManager.AddAsync(Enode, false);
            Assert.That(_staticNodesManager.Nodes.Count(), Is.EqualTo(1));
            Assert.That(await listTask, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task is_static_should_report_correctly()
        {
            Assert.That(_staticNodesManager.IsStatic(Enode), Is.False);
            await _staticNodesManager.AddAsync(Enode, false);
            Assert.That(_staticNodesManager.IsStatic(Enode), Is.True);
        }

        [Test]
        public async Task init_loaded_nodes_are_emitted_with_static_flag()
        {
            await _staticNodesManager.InitAsync();

            List<Node> nodes = await _staticNodesManager.DiscoverNodes(default).Take(2).ToListAsync();

            Assert.That(nodes, Has.Count.EqualTo(2));
            Assert.That(nodes.All(static n => n.IsStatic), Is.True);
        }

        [Test]
        public async Task add_should_emit_node_with_static_flag()
        {
            ValueTask<List<Node>> listTask = _staticNodesManager.DiscoverNodes(default).Take(1).ToListAsync();

            await _staticNodesManager.AddAsync(Enode, false);
            List<Node> nodes = await listTask;

            Assert.That(nodes[0].IsStatic, Is.True);
        }

        [Test]
        public async Task remove_should_delete_an_existing_static_node_and_trigger_an_event()
        {
            bool eventRaised = false;
            _staticNodesManager.NodeRemoved += (s, e) => { eventRaised = true; };
            await _staticNodesManager.AddAsync(Enode, false);
            Assert.That(_staticNodesManager.Nodes.Count(), Is.EqualTo(1));
            await _staticNodesManager.RemoveAsync(Enode, false);
            Assert.That(_staticNodesManager.Nodes.Count(), Is.EqualTo(0));
            Assert.That(eventRaised, Is.True);
        }

        [Test]
        public async Task init_should_load_static_nodes_from_empty_file()
        {
            using TempPath tempFile = TempPath.GetTempFile();
            await File.WriteAllTextAsync(tempFile.Path, string.Empty);
            _staticNodesManager = new StaticNodesManager(tempFile.Path, LimboLogs.Instance);
            await _staticNodesManager.InitAsync();
            Assert.That(_staticNodesManager.Nodes.Count(), Is.EqualTo(0));
        }
    }
}
