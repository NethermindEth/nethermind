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
using System.Reflection;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Config;
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;
using Block = Nethermind.JsonRpc.DataModel.Block;

namespace Nethermind.JsonRpc.Test
{
    [TestFixture]
    public class JsonRpcServiceTests
    {
        [SetUp]
        public void Initialize()
        {
            Assembly jConfig = typeof(JsonRpcConfig).Assembly;
            _configurationProvider = new JsonConfigProvider();
            _logManager = NullLogManager.Instance;
        }

        private IJsonRpcService _jsonRpcService;
        private IConfigProvider _configurationProvider;
        private ILogManager _logManager;

        private JsonRpcResponse TestRequest<T>(IModule ethModule, string method, params string[] parameters) where T : IModule
        {
            RpcModuleProvider moduleProvider = new RpcModuleProvider(_configurationProvider.GetConfig<IJsonRpcConfig>());
            moduleProvider.Register<T>(ethModule);
            _jsonRpcService = new JsonRpcService(moduleProvider, _configurationProvider, _logManager);
            JsonRpcRequest request = RpcTest.GetJsonRequest(method, parameters);
            JsonRpcResponse response = _jsonRpcService.SendRequest(request);
            Assert.AreEqual(request.Id, response.Id);
            return response;
        }

        [Test]
        public void CompileSolidityTest()
        {
            INethmModule nethmModule = Substitute.For<INethmModule>();
            nethmModule.nethm_compileSolidity(Arg.Any<CompilerParameters>()).ReturnsForAnyArgs(r => ResultWrapper<string>.Success(
                "608060405234801561001057600080fd5b5060bb8061001f6000396000f300608060405260043610603f576000357c0100000000000000000000000000000000000000000000000000000000900463ffffffff168063c6888fa1146044575b600080fd5b348015604f57600080fd5b50606c600480360381019080803590602001909291905050506082565b6040518082815260200191505060405180910390f35b60006007820290509190505600a165627a7a72305820cb09d883ac888f0961fd8d82f8dae501d09d54f4bda397e8ca0fb9c05e2ec72a0029"));

            CompilerParameters parameters = new CompilerParameters
            {
                Contract =
                    "pragma solidity ^0.4.22; contract test { function multiply(uint a) public returns(uint d) {   return a * 7;   } }",
                EvmVersion = "byzantium",
                Optimize = false,
                Runs = 2
            };

            JsonRpcResponse response = TestRequest<INethmModule>(nethmModule, "nethm_compileSolidity", parameters.ToJson());

            TestContext.Write(response.Result);
            Assert.IsNotNull(response);
            Assert.IsNull(response.Error, response.Error?.Message);
        }

        [Test]
        public void GetBlockByNumberTest()
        {
            IEthModule ethModule = Substitute.For<IEthModule>();
            ethModule.eth_getBlockByNumber(Arg.Any<BlockParameter>(), true).ReturnsForAnyArgs(x => ResultWrapper<Block>.Success(new Block {Number = new Quantity(2)}));
            JsonRpcResponse response = TestRequest<IEthModule>(ethModule, "eth_getBlockByNumber", "0x1b4", "true");
            Assert.AreEqual((UInt256)2, (response.Result as Block)?.Number?.AsNumber());
        }

        [Test]
        public void GetNewFilterTest()
        {
            IEthModule ethModule = Substitute.For<IEthModule>();
            ethModule.eth_newFilter(Arg.Any<Filter>()).ReturnsForAnyArgs(x => ResultWrapper<BigInteger>.Success(1));

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

            JsonRpcResponse response = TestRequest<IEthModule>(ethModule, "eth_newFilter", JsonConvert.SerializeObject(parameters));
            Assert.AreEqual(BigInteger.One, response.Result);
        }

        [Test]
        public void GetWorkTest()
        {
            IEthModule ethModule = Substitute.For<IEthModule>();
            ethModule.eth_getWork().ReturnsForAnyArgs(x => ResultWrapper<IEnumerable<byte[]>>.Success(new[] {Bytes.FromHexString("aa"), Bytes.FromHexString("01")}));
            JsonRpcResponse response = TestRequest<IEthModule>(ethModule, "eth_getWork");
            byte[][] dataList = response.Result as byte[][];
            Assert.NotNull(dataList?.SingleOrDefault(d => d.ToHexString(true) == "0xaa"));
            Assert.NotNull(dataList?.SingleOrDefault(d => d.ToHexString(true) == "0x01"));
        }

        [Test]
        public void IncorrectMethodNameTest()
        {
            JsonRpcResponse response = TestRequest<IEthModule>(Substitute.For<IEthModule>(), "incorrect_method");
            Assert.AreEqual(response.Error.Code, _configurationProvider.GetConfig<IJsonRpcConfig>().ErrorCodes[ErrorType.MethodNotFound]);
            Assert.IsNull(response.Result);
            Assert.AreEqual(response.JsonRpc, _configurationProvider.GetConfig<IJsonRpcConfig>().JsonRpcVersion);
        }

        [Test]
        public void NetPeerCountTest()
        {
            INetModule netModule = Substitute.For<INetModule>();
            netModule.net_peerCount().ReturnsForAnyArgs(x => ResultWrapper<BigInteger>.Success(new BigInteger(2)));
            JsonRpcResponse response = TestRequest<INetModule>(netModule, "net_peerCount");
            Quantity quantity = new Quantity();
            quantity.FromJson(response.Result.ToString());
            Assert.AreEqual(quantity.AsNumber(), new UInt256(2));
        }

        [Test]
        public void NetVersionTest()
        {
            INetModule netModule = Substitute.For<INetModule>();
            netModule.net_version().ReturnsForAnyArgs(x => ResultWrapper<string>.Success("1"));
            JsonRpcResponse response = TestRequest<INetModule>(netModule, "net_version");
            Assert.AreEqual(response.Result, "1");
            Assert.IsNull(response.Error);
            Assert.AreEqual(_configurationProvider.GetConfig<IJsonRpcConfig>().JsonRpcVersion, response.JsonRpc);
        }

        [Test]
        public void Web3ShaTest()
        {
            IWeb3Module web3Module = Substitute.For<IWeb3Module>();
            web3Module.web3_sha3(Arg.Any<byte[]>()).ReturnsForAnyArgs(x => ResultWrapper<Keccak>.Success(TestObject.KeccakA));
            JsonRpcResponse response = TestRequest<IWeb3Module>(web3Module, "web3_sha3", "0x68656c6c6f20776f726c64");
            Assert.AreEqual(TestObject.KeccakA, response.Result);
        }
    }
}