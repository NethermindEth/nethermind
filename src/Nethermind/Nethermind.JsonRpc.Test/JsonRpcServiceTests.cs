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
using System.Diagnostics.Contracts;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Config;
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;
using Nethermind.KeyStore;
using Newtonsoft.Json;
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
            netModule.net_peerCount().ReturnsForAnyArgs(x => ResultWrapper<Quantity>.Success(new Quantity(2)));

            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            var shhModule = Substitute.For<IShhModule>();
            var nethmModule = Substitute.For<INethmModule>();
            var debugModule = Substitute.For<IDebugModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule,
                nethmModule, debugModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var requestJson = RpcTest.GetJsonRequest("net_peerCount");
            var response = _jsonRpcService.SendRequest(requestJson);
            var quantity = new Quantity();
            quantity.FromJson(response.Result.ToString());
            Assert.AreEqual(quantity.AsNumber(), new UInt256(2));
        }

        [Test]
        public void Web3ShaTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            web3Module.web3_sha3(Arg.Any<Data>()).ReturnsForAnyArgs(x => ResultWrapper<Data>.Success(new Data("abcdef")));
            var shhModule = Substitute.For<IShhModule>();
            var nethmModule = Substitute.For<INethmModule>();
            var debugModule = Substitute.For<IDebugModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule,
                nethmModule, debugModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var requestJson = RpcTest.GetJsonRequest("web3_sha3", "0x68656c6c6f20776f726c64");
            var response = _jsonRpcService.SendRequest(requestJson);
            Assert.AreEqual("0xabcdef", response.Result);
        }

        [Test]
        public void GetBlockByNumberTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            ethModule.eth_getBlockByNumber(Arg.Any<BlockParameter>(), true).ReturnsForAnyArgs(x =>
                ResultWrapper<Block>.Success(new Block {Number = new Quantity(2)}));
            var shhModule = Substitute.For<IShhModule>();
            var nethmModule = Substitute.For<INethmModule>();
            var debugModule = Substitute.For<IDebugModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule,
                nethmModule, debugModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var request = RpcTest.GetJsonRequest("eth_getBlockByNumber", "0x1b4", "true");
            var response = _jsonRpcService.SendRequest(request);

            Assert.IsTrue(response.Result.ToString().Contains("0x02"));
        }

        [Test]
        public void GetWorkTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            ethModule.eth_getWork().ReturnsForAnyArgs(x => ResultWrapper<IEnumerable<Data>>.Success(new[] {new Data("aa"), new Data("01")}));
            var shhModule = Substitute.For<IShhModule>();
            var nethmModule = Substitute.For<INethmModule>();
            var debugModule = Substitute.For<IDebugModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule,
                nethmModule, debugModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var request = RpcTest.GetJsonRequest("eth_getWork");
            var response = _jsonRpcService.SendRequest(request);


            Assert.Contains("0xaa", (List<object>) response.Result);
            Assert.Contains("0x01", (List<object>) response.Result);
        }

        [Test]
        public void NetVersionTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            netModule.net_version().ReturnsForAnyArgs(x => ResultWrapper<string>.Success("1"));
            var shhModule = Substitute.For<IShhModule>();
            var nethmModule = Substitute.For<INethmModule>();
            var debugModule = Substitute.For<IDebugModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule,
                nethmModule, debugModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var request = RpcTest.GetJsonRequest("net_version");
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
            var nethmModule = Substitute.For<INethmModule>();
            var debugModule = Substitute.For<IDebugModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule,
                nethmModule, debugModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var request = RpcTest.GetJsonRequest("incorrect_method");
            var response = _jsonRpcService.SendRequest(request);

            Assert.AreEqual(response.Id, request.Id);
            Assert.AreEqual(response.Error.Code,
                _configurationProvider.GetConfig<IJsonRpcConfig>().ErrorCodes[ErrorType.MethodNotFound]);
            Assert.IsNull(response.Result);
            Assert.AreEqual(response.Jsonrpc, _configurationProvider.GetConfig<IJsonRpcConfig>().JsonRpcVersion);
        }

        [Test]
        public void CompileSolidityTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            var shhModule = Substitute.For<IShhModule>();
            var nethmModule = Substitute.For<INethmModule>();
            var debugModule = Substitute.For<IDebugModule>();
            nethmModule.nethm_compileSolidity(Arg.Any<string>()).ReturnsForAnyArgs(r => ResultWrapper<string>.Success(
                    "608060405234801561001057600080fd5b5060bb8061001f6000396000f300608060405260043610603f576000357c0100000000000000000000000000000000000000000000000000000000900463ffffffff168063c6888fa1146044575b600080fd5b348015604f57600080fd5b50606c600480360381019080803590602001909291905050506082565b6040518082815260200191505060405180910390f35b60006007820290509190505600a165627a7a72305820cb09d883ac888f0961fd8d82f8dae501d09d54f4bda397e8ca0fb9c05e2ec72a0029"));

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule,
                nethmModule, debugModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var parameters = new CompilerParameters
            {
                Contract =
                    "pragma solidity ^0.4.22; contract test { function multiply(uint a) public returns(uint d) {   return a * 7;   } }",
                EvmVersion = "byzantium",
                Optimize = false,
                Runs = 2
            };

            var request = RpcTest.GetJsonRequest("nethm_compileSolidity", parameters.ToJson());

            var response = _jsonRpcService.SendRequest(request);

            TestContext.Write(response.Result);
            Assert.IsNotNull(response);
            Assert.IsNull(response.Error);
        }

        [Test]
        public void GetNewFilterTest()
        {
            var netModule = Substitute.For<INetModule>();
            var ethModule = Substitute.For<IEthModule>();
            var web3Module = Substitute.For<IWeb3Module>();
            ethModule.eth_newFilter(Arg.Any<Filter>()).ReturnsForAnyArgs(x => ResultWrapper<Quantity>.Success(new Quantity("0x01")));
            var shhModule = Substitute.For<IShhModule>();
            var nethmModule = Substitute.For<INethmModule>();
            var debugModule = Substitute.For<IDebugModule>();

            var moduleProvider = new ModuleProvider(_configurationProvider, netModule, ethModule, web3Module, shhModule,
                nethmModule, debugModule);

            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);

            var parameters = new
            {
                fromBlock = "0x1",
                toBlock = "latest",
                address = "0x1f88f1f195afa192cfee860698584c030f4c9db2",
                topics = new List<object>
                {
                    "0x000000000000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b", null,
                    new[]
                    {
                        "0x000000000000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b",
                        "0x0000000000000000000000000aff3454fce5edbc8cca8697c15331677e6ebccc"
                    }
                }
            };
            var request = RpcTest.GetJsonRequest("eth_newFilter", JsonConvert.SerializeObject(parameters));
            var response = _jsonRpcService.SendRequest(request);

            Assert.AreEqual("0x01", response.Result);
        }
    }
}