using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Dsl.Test
{
    public class DslRpcModuleTests
    {
        private DslPlugin _dslPlugin;
        private INethermindApi _api;
        private Dictionary<int, Interpreter> _interpreter;
        private DslRpcModule _dslRpcModule;
        private ILogger logger;

        [SetUp]
        public void SetUp()
        {
            _dslPlugin = new DslPlugin();
            _api = Substitute.For<INethermindApi>();
            _dslRpcModule = Substiture.For<DslRpcModule>();
            _interpreter = Substiture.For<Dictionary<int, Interpreter>>();
            _logger = Substiture.For<ILogger>();
        }
        
        [Test]
        public async Task will_throw_error_for_undefined_watch(String script)
        {
           _dslRpcModule.dsl_addScript("WATCH House WHERE Author IS 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd PUBLISH WebSockets ytext");
           await _dslPlugin.Init(_api);
        }

        [Test]
        public async Task will_throw_error_for_improper_use_of_is(String script)
        {
            _dslRpcModule.dsl_addScript("WATCH Blocks WHERE Author IS AUTHOR PUBLISH WebSockets ytext");
            await _dslPlugin.Init(_api);
        }


        [Test]
        public async Task will_return_nothing_if_is_is_always_false(String script)
        {
            _dslRpcModule.dsl_addScript("WATCH WHERE 23 IS NOT 23 PUBLISH WebSockets ytext");
            await _dslPlugin.Init(_api);
        }


        [Test]
        public async Task will_fail_with_out_of_order_expressions(String script)
        {
            _dslRpcModule.dsl_addScript("WHERE Author IS 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd WATCH Blocks PUBLISH WebSockets ytext");
            await _dslPlugin.Init(_api);
        }

        [Test]
        public async Task will_fail_on_undefined_operation(String script)
        {
            _dslRpcModule.dsl_addScript("WATCH Blocks WHERE AUTHOR IS 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd STOP PUBLISH WebSockets ytext");
            await _dslPlugin.Init(_api);
        }
    }
}
