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
using System.Net.Http;
using System.Numerics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.JsonRpc.Config;
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
        private ILogManager _logManager;

        [SetUp]
        public void Initialize()
        {
            var jConfig = typeof(JsonRpcConfig).Assembly;
            _configurationProvider = new JsonConfigProvider();         
            _logManager = NullLogManager.Instance;
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

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var requestJson = GetJsonRequest("net_peerCount", null); 
            var response = _jsonRpcService.SendRequest(requestJson);
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
            web3Module.web3_sha3(Arg.Any<Data>()).ReturnsForAnyArgs(x => new ResultWrapper<Data> { Result = new Result { ResultType = ResultType.Success }, Data = new Data("abcdef") });
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var requestJson = GetJsonRequest("web3_sha3", new[] { "0x68656c6c6f20776f726c64" });
            var response = _jsonRpcService.SendRequest(requestJson);
            Assert.AreEqual("0xabcdef", response.Result);
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

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var request = GetJsonRequest("eth_getBlockByNumber", new[] {"0x1b4", "true"});
            var response = _jsonRpcService.SendRequest(request);

            Assert.IsTrue(response.Result.ToString().Contains("0x02"));
        }

        [Test]
        public void GetWorkTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            ethModule.eth_getWork().ReturnsForAnyArgs(x => new ResultWrapper<IEnumerable<Data>> { Result = new Result { ResultType = ResultType.Success }, Data = new [] { new Data("aa"), new Data("01")   } });
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var request = GetJsonRequest("eth_getWork", null);
            var response = _jsonRpcService.SendRequest(request);

            
            
            Assert.Contains("0xaa", (List<object>)response.Result);
            Assert.Contains("0x01", (List<object>)response.Result);
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

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var request = GetJsonRequest("net_version", null);
            var response = _jsonRpcService.SendRequest(request);

            Assert.AreEqual(response.Id, request.Id);
            Assert.AreEqual(response.Result, "1");
            Assert.IsNull(response.Error);
            Assert.AreEqual(_configurationProvider.GetConfig<IJsonRpcConfig>().JsonRpcVersion, response.Jsonrpc);
        }

        [Test]
        public void IncorrectMethodNameTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            var shhModule = Substitute.For<IShhModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var request = GetJsonRequest("incorrect_method", null);
            var response = _jsonRpcService.SendRequest(request);

            Assert.AreEqual(response.Id, request.Id);
            Assert.AreEqual(response.Error.Code, _configurationProvider.GetConfig<IJsonRpcConfig>().ErrorCodes[ErrorType.MethodNotFound]);
            Assert.IsNull(response.Result);
            Assert.AreEqual(response.Jsonrpc, _configurationProvider.GetConfig<IJsonRpcConfig>().JsonRpcVersion);
        }

        //{
        //    "jsonrpc": "2.0",
        //    "method": "eth_getBlockByNumber",
        //    "params": [ "0x1b4", true ],
        //    "id": 67
        //}
        public JsonRpcRequest GetJsonRequest(string method, IEnumerable<string> parameters)
        {
            var request = new JsonRpcRequest()
            {
                Jsonrpc = "2.0",
                Method = method,
                Params = parameters?.ToArray() ?? new string[0],
                Id = "67"
            };
            
            return request;
        }
    }
}
