using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Json;
using Nevermind.JsonRpc.DataModel;
using Nevermind.JsonRpc.Module;
using Nevermind.Utils.Model;
using NSubstitute;
using NUnit.Framework;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Block = Nevermind.JsonRpc.DataModel.Block;

namespace Nevermind.JsonRpc.Test
{
    [TestFixture]
    public class JsonRpcServiceTests
    {
        private IJsonRpcService _jsonRpcService;
        private IConfigurationProvider _configurationProvider;
        private IJsonSerializer _jsonSerializer;
        private ILogger _logger;

        [SetUp]
        public void Initialize()
        {
            _configurationProvider = new ConfigurationProvider();            
            _logger = new ConsoleLogger();
            _jsonSerializer = new JsonSerializer(_logger);
        }

        [Test]
        public void NetPeerCountTest()
        {
            var netModule = Substitute.For<INetModule>();
            netModule.net_peerCount().ReturnsForAnyArgs(x => new ResultWrapper<Quantity>{Result = new Result{ResultType = ResultType.Success}, Data = new Quantity(2)});

            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonSerializer = new JsonSerializer(_logger);
            _jsonRpcService = new JsonRpcService(_configurationProvider, _logger, _jsonSerializer, moduleProvider);

            var requestJson = GetJsonRequest("net_peerCount", null); 
            var rawResponse = _jsonRpcService.SendRequest(requestJson);
            var response = _jsonSerializer.DeserializeObject<JsonRpcResponse>(rawResponse);
            var quantity = new Quantity();
            quantity.FromJson(response.Result.ToString());
            Assert.AreEqual(quantity.GetValue(), new BigInteger(2));
        }

        [Test]
        public void Web3ShaTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            web3Module.web3_sha3(Arg.Any<Data>()).ReturnsForAnyArgs(x => new ResultWrapper<Data> { Result = new Result { ResultType = ResultType.Success }, Data = new Data("test data") });
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonSerializer = new JsonSerializer(_logger);
            _jsonRpcService = new JsonRpcService(_configurationProvider, _logger, _jsonSerializer, moduleProvider);

            var requestJson = GetJsonRequest("web3_sha3", new[] { "0x68656c6c6f20776f726c64" });
            var rawResponse = _jsonRpcService.SendRequest(requestJson);
            var response = _jsonSerializer.DeserializeObject<JsonRpcResponse>(rawResponse);
            Assert.AreEqual(response.Result, "0xtest data");
        }

        [Test]
        public void GetBlockByNumberTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            ethModule.eth_getBlockByNumber(Arg.Any<BlockParameter>(), true).ReturnsForAnyArgs(x => new ResultWrapper<Block> { Result = new Result { ResultType = ResultType.Success }, Data = new Block{Number = new Quantity(2)} });
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonSerializer = new JsonSerializer(_logger);
            _jsonRpcService = new JsonRpcService(_configurationProvider, _logger, _jsonSerializer, moduleProvider);

            var requestJson = GetJsonRequest("eth_getBlockByNumber", new[] {"0x1b4", "true"});
            var rawResponse = _jsonRpcService.SendRequest(requestJson);
            var response = _jsonSerializer.DeserializeObject<JsonRpcResponse>(rawResponse);

            Assert.IsTrue(response.Result.ToString().Contains("0x2"));
        }

        [Test]
        public void GetWorkTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            ethModule.eth_getWork().ReturnsForAnyArgs(x => new ResultWrapper<IEnumerable<Data>> { Result = new Result { ResultType = ResultType.Success }, Data = new [] { new Data("t1"), new Data("t2")   } });
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonSerializer = new JsonSerializer(_logger);
            _jsonRpcService = new JsonRpcService(_configurationProvider, _logger, _jsonSerializer, moduleProvider);

            var requestJson = GetJsonRequest("eth_getWork", null);
            var rawResponse = _jsonRpcService.SendRequest(requestJson);
            var response = _jsonSerializer.DeserializeObject<JsonRpcResponse>(rawResponse);

            Assert.IsTrue(response.Result.ToString().Contains("0xt1"));
            Assert.IsTrue(response.Result.ToString().Contains("0xt2"));
        }

        [Test]
        public void NetVersionTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            netModule.net_version().ReturnsForAnyArgs(x => new ResultWrapper<string> { Result = new Result { ResultType = ResultType.Success }, Data = "1" });
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonSerializer = new JsonSerializer(_logger);
            _jsonRpcService = new JsonRpcService(_configurationProvider, _logger, _jsonSerializer, moduleProvider);

            var requestJson = GetJsonRequest("net_version", null);
            var rawResponse = _jsonRpcService.SendRequest(requestJson);

            var request = _jsonSerializer.DeserializeObject<JsonRpcRequest>(requestJson);
            var response = _jsonSerializer.DeserializeObject<JsonRpcResponse>(rawResponse);

            Assert.AreEqual(response.Id, request.Id);
            Assert.AreEqual(response.Result, "1");
            Assert.IsNull(response.Error);
            Assert.AreEqual(response.Jsonrpc, _configurationProvider.JsonRpcVersion);
        }

        [Test]
        public void IncorrectMethodNameTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonSerializer = new JsonSerializer(_logger);
            _jsonRpcService = new JsonRpcService(_configurationProvider, _logger, _jsonSerializer, moduleProvider);

            var requestJson = GetJsonRequest("incorrect_method", null);
            var rawResponse = _jsonRpcService.SendRequest(requestJson);

            var request = _jsonSerializer.DeserializeObject<JsonRpcRequest>(requestJson);
            var response = _jsonSerializer.DeserializeObject<JsonRpcResponse>(rawResponse);

            Assert.AreEqual(response.Id, request.Id);
            Assert.AreEqual(response.Error.Code, _configurationProvider.ErrorCodes[ErrorType.MethodNotFound]);
            Assert.IsNull(response.Result);
            Assert.AreEqual(response.Jsonrpc, _configurationProvider.JsonRpcVersion);
        }

        //{
        //    "jsonrpc": "2.0",
        //    "method": "eth_getBlockByNumber",
        //    "params": [ "0x1b4", true ],
        //    "id": 67
        //}
        public string GetJsonRequest(string method, IEnumerable<object> parameters)
        {
            var request = new
            {
                jsonrpc = "2.0",
                method,
                Params = parameters ?? Enumerable.Empty<object>(),
                id = 67
            };
            return _jsonSerializer.SerializeObject(request);
        }
    }
}
