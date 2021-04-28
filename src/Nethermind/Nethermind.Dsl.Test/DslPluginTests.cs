using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Processing;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Dsl.Test
{
    public class DslPluginTests
    {
        private DslPlugin _dslPlugin;
        private INethermindApi _api;

        [SetUp]
        public void SetUp()
        {
            _dslPlugin = new DslPlugin();
            _api = Substitute.For<INethermindApi>();
        }


        [Test]
        public async Task can_init_plugin_with_tree_listener()
        {
            await _dslPlugin.Init(_api);
        }

        [Test]
        public async Task will_filter_block_correctly_depending_on_condition()
        {
            var header = Build.A.BlockHeader.WithAuthor(TestItem.AddressA).TestObject;
            var block = Build.A.Block.WithHeader(header).TestObject;

            var header2 = Build.A.BlockHeader.WithAuthor(TestItem.AddressB).TestObject;
            var block2 = Build.A.Block.WithHeader(header2).TestObject;

            await _dslPlugin.Init(_api);

            _api.MainBlockProcessor.BlockProcessed += Raise.EventWith(null, new BlockProcessedEventArgs(block, null));
        }
    }
}