/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;
using Nethermind.KeyStore;
using NSubstitute;
using NUnit.Framework;
using Block = Nethermind.JsonRpc.DataModel.Block;

namespace Nethermind.JsonRpc.Test
{
    [TestFixture]
    public class JsonRpcServiceTests
    {
        private IJsonRpcService _jsonRpcService;
        private IConfigProvider _configurationProvider;
        private IJsonSerializer _jsonSerializer;
        private ILogManager _logManager;

        [SetUp]
        public void Initialize()
        {
            _configurationProvider = new JsonConfigProvider();         
            _logManager = NullLogManager.Instance;
            _jsonSerializer = new JsonSerializer(_logManager);
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

            _jsonSerializer = new JsonSerializer(_logManager);
            _jsonRpcService = new JsonRpcService(_jsonSerializer, moduleProvider, _configurationProvider, _logManager);

            var requestJson = GetJsonRequest("net_peerCount", null); 
            var rawResponse = _jsonRpcService.SendRequest(requestJson);
            var response = _jsonSerializer.Deserialize<JsonRpcResponse>(rawResponse);
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

            _jsonSerializer = new JsonSerializer(_logManager);
            _jsonRpcService = new JsonRpcService(_jsonSerializer, moduleProvider, _configurationProvider, _logManager);

            var requestJson = GetJsonRequest("web3_sha3", new[] { "0x68656c6c6f20776f726c64" });
            var rawResponse = _jsonRpcService.SendRequest(requestJson);
            var response = _jsonSerializer.Deserialize<JsonRpcResponse>(rawResponse);
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

            _jsonSerializer = new JsonSerializer(_logManager);
            _jsonRpcService = new JsonRpcService(_jsonSerializer, moduleProvider, _configurationProvider, _logManager);

            var requestJson = GetJsonRequest("eth_getBlockByNumber", new[] {"0x1b4", "true"});
            var rawResponse = _jsonRpcService.SendRequest(requestJson);
            var response = _jsonSerializer.Deserialize<JsonRpcResponse>(rawResponse);

            Assert.IsTrue(response.Result.ToString().Contains("0x02"));
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

            _jsonSerializer = new JsonSerializer(_logManager);
            _jsonRpcService = new JsonRpcService(_jsonSerializer, moduleProvider, _configurationProvider, _logManager);

            var requestJson = GetJsonRequest("eth_getWork", null);
            var rawResponse = _jsonRpcService.SendRequest(requestJson);
            var response = _jsonSerializer.Deserialize<JsonRpcResponse>(rawResponse);

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

            _jsonSerializer = new JsonSerializer(_logManager);
            _jsonRpcService = new JsonRpcService(_jsonSerializer, moduleProvider, _configurationProvider, _logManager);

            var requestJson = GetJsonRequest("net_version", null);
            var rawResponse = _jsonRpcService.SendRequest(requestJson);

            var request = _jsonSerializer.Deserialize<JsonRpcRequest>(requestJson);
            var response = _jsonSerializer.Deserialize<JsonRpcResponse>(rawResponse);

            Assert.AreEqual(response.Id, request.Id);
            Assert.AreEqual(response.Result, "1");
            Assert.IsNull(response.Error);
            Assert.AreEqual(response.Jsonrpc, _configurationProvider.JsonRpcConfig.JsonRpcVersion);
        }

        [Test]
        public void IncorrectMethodNameTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonSerializer = new JsonSerializer(_logManager);
            _jsonRpcService = new JsonRpcService(_jsonSerializer, moduleProvider, _configurationProvider, _logManager);

            var requestJson = GetJsonRequest("incorrect_method", null);
            var rawResponse = _jsonRpcService.SendRequest(requestJson);

            var request = _jsonSerializer.Deserialize<JsonRpcRequest>(requestJson);
            var response = _jsonSerializer.Deserialize<JsonRpcResponse>(rawResponse);

            Assert.AreEqual(response.Id, request.Id);
            Assert.AreEqual(response.Error.Code, _configurationProvider.JsonRpcConfig.ErrorCodes[(ConfigErrorType)ErrorType.MethodNotFound]);
            Assert.IsNull(response.Result);
            Assert.AreEqual(response.Jsonrpc, _configurationProvider.JsonRpcConfig.JsonRpcVersion);
        }

        [Test]
        public void RequestCollectionTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            netModule.net_version().ReturnsForAnyArgs(x => new ResultWrapper<string> { Result = new Result { ResultType = ResultType.Success }, Data = "1" });
            ethModule.eth_protocolVersion().ReturnsForAnyArgs(x => new ResultWrapper<string> { Result = new Result { ResultType = ResultType.Success }, Data = "1" });
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonSerializer = new JsonSerializer(_logManager);
            _jsonRpcService = new JsonRpcService(_jsonSerializer, moduleProvider, _configurationProvider, _logManager);

            var netRequestJson = GetJsonRequest("net_version", null);
            var ethRequestJson = GetJsonRequest("eth_protocolVersion", null);

            var jsonRequest = $"[{netRequestJson},{ethRequestJson}]";
            var rawResponse = _jsonRpcService.SendRequest(jsonRequest);         
            var response = _jsonSerializer.DeserializeObjectOrArray<JsonRpcResponse>(rawResponse);

            Assert.IsNotNull(response);
            Assert.IsNotNull(response.Collection);
            Assert.IsNull(response.Model);
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
            return _jsonSerializer.Serialize(request);
        }
    }
}
