//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using FluentAssertions.Specialized;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class JsonRpcServiceTests
    {
        [SetUp]
        public void Initialize()
        {
            Assembly jConfig = typeof(JsonRpcConfig).Assembly;
            _configurationProvider = new ConfigProvider();
            _logManager = LimboLogs.Instance;
        }

        private IJsonRpcService _jsonRpcService;
        private IConfigProvider _configurationProvider;
        private ILogManager _logManager;

        private JsonRpcResponse TestRequest<T>(T module, string method, params string[] parameters) where T : IRpcModule
        {
            RpcModuleProvider moduleProvider = new RpcModuleProvider(new FileSystem(), _configurationProvider.GetConfig<IJsonRpcConfig>(), LimboLogs.Instance);
            moduleProvider.Register(new SingletonModulePool<T>(new SingletonFactory<T>(module), true));
            _jsonRpcService = new JsonRpcService(moduleProvider, _logManager);
            JsonRpcRequest request = RpcTest.GetJsonRequest(method, parameters);
            JsonRpcResponse response = _jsonRpcService.SendRequestAsync(request, JsonRpcContext.Http).Result;
            Assert.AreEqual(request.Id, response.Id);
            return response;
        }

        [Test]
        public void GetBlockByNumberTest()
        {
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            ethRpcModule.eth_getBlockByNumber(Arg.Any<BlockParameter>(), true).ReturnsForAnyArgs(x => ResultWrapper<BlockForRpc>.Success(new BlockForRpc(Build.A.Block.WithNumber(2).TestObject, true, specProvider)));
            JsonRpcSuccessResponse response = TestRequest(ethRpcModule, "eth_getBlockByNumber", "0x1b4", "true") as JsonRpcSuccessResponse;
            Assert.AreEqual(2L, (response?.Result as BlockForRpc)?.Number);
        }
        
        [Test]
        public void Eth_module_populates_size_when_returning_block_data()
        {
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            ethRpcModule.eth_getBlockByNumber(Arg.Any<BlockParameter>(), true).ReturnsForAnyArgs(x => ResultWrapper<BlockForRpc>.Success(new BlockForRpc(Build.A.Block.WithNumber(2).TestObject, true, specProvider)));
            JsonRpcSuccessResponse response = TestRequest(ethRpcModule, "eth_getBlockByNumber", "0x1b4", "true") as JsonRpcSuccessResponse;
            Assert.AreEqual(513L, (response?.Result as BlockForRpc)?.Size);
        }
        
        [Test]
        public void CanHandleOptionalArguments()
        {
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string serialized = serializer.Serialize(new TransactionForRpc());
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_call(Arg.Any<TransactionForRpc>()).ReturnsForAnyArgs(x => ResultWrapper<string>.Success("0x1"));
            JsonRpcSuccessResponse response = TestRequest(ethRpcModule, "eth_call", serialized) as JsonRpcSuccessResponse;
            Assert.AreEqual("0x1", response?.Result);
        }
        
        [Test]
        public void Case_sensitivity_test()
        {
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_chainId().ReturnsForAnyArgs(ResultWrapper<ulong>.Success(1ul));
            TestRequest(ethRpcModule, "eth_chainID").Should().BeOfType<JsonRpcErrorResponse>();
            TestRequest(ethRpcModule, "eth_chainId").Should().BeOfType<JsonRpcSuccessResponse>();
        }
        
        [Test]
        public void GetNewFilterTest()
        {
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_newFilter(Arg.Any<Filter>()).ReturnsForAnyArgs(x => ResultWrapper<UInt256?>.Success(1));

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

            JsonRpcSuccessResponse response = TestRequest(ethRpcModule, "eth_newFilter", JsonConvert.SerializeObject(parameters)) as JsonRpcSuccessResponse;
            Assert.AreEqual(UInt256.One, response?.Result);
        }

        [Test]
        public void GetWorkTest()
        {
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_getWork().ReturnsForAnyArgs(x => ResultWrapper<IEnumerable<byte[]>>.Success(new[] {Bytes.FromHexString("aa"), Bytes.FromHexString("01")}));
            JsonRpcSuccessResponse response = TestRequest(ethRpcModule, "eth_getWork") as JsonRpcSuccessResponse;
            byte[][] dataList = response?.Result as byte[][];
            Assert.NotNull(dataList?.SingleOrDefault(d => d.ToHexString(true) == "0xaa"));
            Assert.NotNull(dataList?.SingleOrDefault(d => d.ToHexString(true) == "0x01"));
        }

        [Test]
        public void IncorrectMethodNameTest()
        {
            JsonRpcErrorResponse response = TestRequest(Substitute.For<IEthRpcModule>(), "incorrect_method") as JsonRpcErrorResponse;
            Assert.AreEqual(response?.Error.Code, ErrorCodes.MethodNotFound);
        }

        [Test]
        public void NetVersionTest()
        {
            INetRpcModule netRpcModule = Substitute.For<INetRpcModule>();
            netRpcModule.net_version().ReturnsForAnyArgs(x => ResultWrapper<string>.Success("1"));
            JsonRpcSuccessResponse response = TestRequest(netRpcModule, "net_version") as JsonRpcSuccessResponse;
            Assert.AreEqual(response?.Result, "1");
            Assert.IsNotInstanceOf<JsonRpcErrorResponse>(response);
        }

        [Test]
        public void Web3ShaTest()
        {
            IWeb3RpcModule web3RpcModule = Substitute.For<IWeb3RpcModule>();
            web3RpcModule.web3_sha3(Arg.Any<byte[]>()).ReturnsForAnyArgs(x => ResultWrapper<Keccak>.Success(TestItem.KeccakA));
            JsonRpcSuccessResponse response = TestRequest(web3RpcModule, "web3_sha3", "0x68656c6c6f20776f726c64") as JsonRpcSuccessResponse;
            Assert.AreEqual(TestItem.KeccakA, response?.Result);
        }
    }
}
