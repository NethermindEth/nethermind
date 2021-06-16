using System;
using System.Linq;
using System.Data;
using System.Collections.Generic;
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
using Nethermind.JsonRpc;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Dsl.ANTLR;
using Nethermind.Dsl.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Dsl.Test
{
    public class DslRpcModuleTests
    {
        private DslPlugin _dslPlugin;
        private INethermindApi _api;
        private Dictionary<int, Interpreter> _interpreter;
        private DslRpcModule _dslRpcModule;
        private ILogger _logger;
        public ResultWrapper<int> res;

        [SetUp]
        public void SetUp()
        {
             _api = Substitute.For<INethermindApi>();
            _interpreter = Substitute.For<Dictionary<int, Interpreter>>();
            _logger = Substitute.For<ILogger>();

            _dslPlugin = new DslPlugin();
            _dslRpcModule = new DslRpcModule(_api, _logger, _interpreter);
        }
        
        [Test]
        public async Task check()
        {
           res = _dslRpcModule.dsl_addScript("WATCH Blocks WHERE Author IS 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd PUBLISH WebSockets ytext");
           Assert.IsInstanceOf<ResultWrapper<int>>(res);
        }
                        
        [Test]
        public async Task will_throw_error_for_undefined_watch()
        {
           res = _dslRpcModule.dsl_addScript("WATCH House WHERE Author IS 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd PUBLISH WebSockets ytext");
           Assert.IsInstanceOf<ResultWrapper<int>>(res);
        }

        [Test]
        public async Task will_throw_error_for_improper_use_of_is()
        {
            res = _dslRpcModule.dsl_addScript("WATCH Blocks WHERE Author IS AUTHOR PUBLISH WebSockets ytext");
            Assert.IsInstanceOf<ResultWrapper<int>>(res);
        }


        [Test]
        public async Task will_return_nothing_if_is_is_always_false()
        {
            res = _dslRpcModule.dsl_addScript("WATCH WHERE 23 IS NOT 23 PUBLISH WebSockets ytext");
            Assert.IsInstanceOf<ResultWrapper<int>>(res);
        }


        [Test]
        public async Task will_fail_with_out_of_order_expressions()
        {
            res = _dslRpcModule.dsl_addScript("WHERE Author IS 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd WATCH Blocks PUBLISH WebSockets ytext");
            Assert.IsInstanceOf<ResultWrapper<int>>(res);
        }

        [Test]
        public async Task will_fail_on_undefined_operation()
        {
            res = _dslRpcModule.dsl_addScript("WATCH Blocks WHERE AUTHOR IS 0x22eA9f6b28DB76A7162054c05ed812dEb2f519Cd STOP PUBLISH WebSockets ytext");
            Assert.IsInstanceOf<ResultWrapper<int>>(res);
        }
    }
}   
