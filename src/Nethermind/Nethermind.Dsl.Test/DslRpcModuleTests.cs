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
        public async Task undefined_watch_test(String script)
        {
           _dslRpcModule.dsl_addScript("WATCH House WHERE Author IS 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd PUBLISH WebSockets ytext");
           await _dslPlugin.Init(_api);
        }

        [Test]
        public async Task undefined_condition_test(String script)
        {
            _dslRpcModule.dsl_addScript("WATCH Blocks WHERE Author IS AUTHOR PUBLISH WebSockets ytext");
            await _dslPlugin.Init(_api);
        }


        [Test]
        public async Task undefined_operation_test(String script)
        {
            _dslRpcModule.dsl_addScript("WATCH WHERE 23 IS NOT 23 PUBLISH WebSockets ytext");
            await _dslPlugin.Init(_api);
        }


        [Test]
        public async Task undefined_syntax_test(String script)
        {
            _dslRpcModule.dsl_addScript("WHERE Author 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd WATCH Blocks PUBLISH WebSockets ytext");
            await _dslPlugin.Init(_api);
        }

        [Test]
        public async Task undefined_condition_test(String script)
        {
            _dslRpcModule.dsl_addScript("WATCH WHERE AUTHOR is 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd PUBLISH WebSockets ytext");
            await _dslPlugin.Init(_api);
        }
    }
}
