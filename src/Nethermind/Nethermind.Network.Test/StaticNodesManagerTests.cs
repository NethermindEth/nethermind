using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Logging;
using Nethermind.Network.StaticNodes;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    public class StaticNodesManagerTests
    {
        private IStaticNodesManager _staticNodesManager;

        private const string Enode =
            "enode://94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09@192.81.208.223:30303";

        [SetUp]
        public void Setup()
        {
            var path = "test-static-nodes.json";
            var logManager = NullLogManager.Instance;
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
    }
}