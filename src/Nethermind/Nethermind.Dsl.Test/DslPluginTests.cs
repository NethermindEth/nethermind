using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Dsl.Test
{
    public class DslPluginTests
    {
        private INethermindPlugin _dslPlugin;
        private INethermindApi _api;

        [SetUp]
        public void SetUp()
        {
            _dslPlugin = new DslPlugin();
            _api = Substitute.For<INethermindApi>();
        }


        [Test]
        public void can_init_plugin_with_tree_listener()
        {
            _dslPlugin.Init(_api);
        }
    }
}