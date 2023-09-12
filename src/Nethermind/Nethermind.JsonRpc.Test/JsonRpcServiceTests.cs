// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Proxy.Models.MultiCall;
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
            _context = new JsonRpcContext(RpcEndpoint.Http);
        }

        private IJsonRpcService _jsonRpcService = null!;
        private IConfigProvider _configurationProvider = null!;
        private ILogManager _logManager = null!;
        private JsonRpcContext _context = null!;

        private JsonRpcResponse TestRequest<T>(T module, string method, params string[] parameters) where T : IRpcModule
        {
            RpcModuleProvider moduleProvider = new(new FileSystem(), _configurationProvider.GetConfig<IJsonRpcConfig>(), LimboLogs.Instance);
            moduleProvider.Register(new SingletonModulePool<T>(new SingletonFactory<T>(module), true));
            _jsonRpcService = new JsonRpcService(moduleProvider, _logManager, _configurationProvider.GetConfig<IJsonRpcConfig>());
            JsonRpcRequest request = RpcTest.GetJsonRequest(method, parameters);
            JsonRpcResponse response = _jsonRpcService.SendRequestAsync(request, _context).Result;
            Assert.That(response.Id, Is.EqualTo(request.Id));
            return response;
        }

        [Test]
        public void GetBlockByNumberTest()
        {
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            ethRpcModule.eth_getBlockByNumber(Arg.Any<BlockParameter>(), true).ReturnsForAnyArgs(x => ResultWrapper<BlockForRpc>.Success(new BlockForRpc(Build.A.Block.WithNumber(2).TestObject, true, specProvider)));
            JsonRpcSuccessResponse? response = TestRequest(ethRpcModule, "eth_getBlockByNumber", "0x1b4", "true") as JsonRpcSuccessResponse;
            Assert.That((response?.Result as BlockForRpc)?.Number, Is.EqualTo(2L));
        }

        [Test]
        public void Eth_module_populates_size_when_returning_block_data()
        {
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            ethRpcModule.eth_getBlockByNumber(Arg.Any<BlockParameter>(), true).ReturnsForAnyArgs(x => ResultWrapper<BlockForRpc>.Success(new BlockForRpc(Build.A.Block.WithNumber(2).TestObject, true, specProvider)));
            JsonRpcSuccessResponse? response = TestRequest(ethRpcModule, "eth_getBlockByNumber", "0x1b4", "true") as JsonRpcSuccessResponse;
            Assert.That((response?.Result as BlockForRpc)?.Size, Is.EqualTo(513L));
        }


        [Test]
        public void CanRunEthMulticallV1Empty()
        {
            MultiCallPayload<TransactionForRpc> payload = new() { BlockStateCalls = Array.Empty<BlockStateCall<TransactionForRpc>>() };

            EthereumJsonSerializer serializer = new();

            string serializedCall = serializer.Serialize(payload);

            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_multicallV1(payload).ReturnsForAnyArgs(x =>
                ResultWrapper<IReadOnlyList<MultiCallBlockResult>>.Success(Array.Empty<MultiCallBlockResult>()));
            JsonRpcSuccessResponse? response =
                TestRequest(ethRpcModule, "eth_multicallV1", serializedCall) as JsonRpcSuccessResponse;
            Assert.IsTrue(response != null);
            Assert.That(response?.Result, Is.EqualTo(Array.Empty<MultiCallBlockResult>()));
        }


        [Test]
        public void CanHandleOptionalArguments()
        {
            EthereumJsonSerializer serializer = new();
            string serialized = serializer.Serialize(new TransactionForRpc());
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_call(Arg.Any<TransactionForRpc>()).ReturnsForAnyArgs(x => ResultWrapper<string>.Success("0x1"));
            JsonRpcSuccessResponse? response = TestRequest(ethRpcModule, "eth_call", serialized) as JsonRpcSuccessResponse;
            Assert.That(response?.Result, Is.EqualTo("0x1"));
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
                    "0x000000000000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b", null!,
                new[]
                {
                    "0x000000000000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b",
                    "0x0000000000000000000000000aff3454fce5edbc8cca8697c15331677e6ebccc"
                }
            }
            };

            JsonRpcSuccessResponse? response = TestRequest(ethRpcModule, "eth_newFilter", JsonConvert.SerializeObject(parameters)) as JsonRpcSuccessResponse;
            Assert.That(response?.Result, Is.EqualTo(UInt256.One));
        }

        [Test]
        public void Eth_call_is_working_with_implicit_null_as_the_last_argument()
        {
            EthereumJsonSerializer serializer = new();
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_call(Arg.Any<TransactionForRpc>(), Arg.Any<BlockParameter?>()).ReturnsForAnyArgs(x => ResultWrapper<string>.Success("0x"));

            string serialized = serializer.Serialize(new TransactionForRpc());

            JsonRpcSuccessResponse? response = TestRequest(ethRpcModule, "eth_call", serialized) as JsonRpcSuccessResponse;
            Assert.That(response?.Result, Is.EqualTo("0x"));
        }

        [TestCase("")]
        [TestCase(null)]
        public void Eth_call_is_working_with_explicit_null_as_the_last_argument(string nullValue)
        {
            EthereumJsonSerializer serializer = new();
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_call(Arg.Any<TransactionForRpc>(), Arg.Any<BlockParameter?>()).ReturnsForAnyArgs(x => ResultWrapper<string>.Success("0x"));

            string serialized = serializer.Serialize(new TransactionForRpc());

            JsonRpcSuccessResponse? response = TestRequest(ethRpcModule, "eth_call", serialized, nullValue) as JsonRpcSuccessResponse;
            Assert.That(response?.Result, Is.EqualTo("0x"));
        }

        [Test]
        public void GetWorkTest()
        {
            IEthRpcModule ethRpcModule = Substitute.For<IEthRpcModule>();
            ethRpcModule.eth_getWork().ReturnsForAnyArgs(x => ResultWrapper<IEnumerable<byte[]>>.Success(new[] { Bytes.FromHexString("aa"), Bytes.FromHexString("01") }));
            JsonRpcSuccessResponse? response = TestRequest(ethRpcModule, "eth_getWork") as JsonRpcSuccessResponse;
            byte[][]? dataList = response?.Result as byte[][];
            Assert.NotNull(dataList?.SingleOrDefault(d => d.ToHexString(true) == "0xaa"));
            Assert.NotNull(dataList?.SingleOrDefault(d => d.ToHexString(true) == "0x01"));
        }

        [Test]
        public void IncorrectMethodNameTest()
        {
            JsonRpcErrorResponse? response = TestRequest(Substitute.For<IEthRpcModule>(), "incorrect_method") as JsonRpcErrorResponse;
            Assert.That(response?.Error?.Code, Is.EqualTo(ErrorCodes.MethodNotFound));
        }

        [Test]
        public void NetVersionTest()
        {
            INetRpcModule netRpcModule = Substitute.For<INetRpcModule>();
            netRpcModule.net_version().ReturnsForAnyArgs(x => ResultWrapper<string>.Success("1"));
            JsonRpcSuccessResponse? response = TestRequest(netRpcModule, "net_version") as JsonRpcSuccessResponse;
            Assert.That(response?.Result, Is.EqualTo("1"));
            Assert.IsNotInstanceOf<JsonRpcErrorResponse>(response);
        }

        [Test]
        public void Web3ShaTest()
        {
            IWeb3RpcModule web3RpcModule = Substitute.For<IWeb3RpcModule>();
            web3RpcModule.web3_sha3(Arg.Any<byte[]>()).ReturnsForAnyArgs(x => ResultWrapper<Keccak>.Success(TestItem.KeccakA));
            JsonRpcSuccessResponse? response = TestRequest(web3RpcModule, "web3_sha3", "0x68656c6c6f20776f726c64") as JsonRpcSuccessResponse;
            Assert.That(response?.Result, Is.EqualTo(TestItem.KeccakA));
        }

        [TestCaseSource(nameof(BlockForRpcTestSource))]
        public void BlockForRpc_should_expose_withdrawals_if_any((bool Expected, Block Block) item)
        {
            var specProvider = Substitute.For<ISpecProvider>();
            var rpcBlock = new BlockForRpc(item.Block, false, specProvider);

            rpcBlock.WithdrawalsRoot.Should().BeEquivalentTo(item.Block.WithdrawalsRoot);
            rpcBlock.Withdrawals.Should().BeEquivalentTo(item.Block.Withdrawals);

            var json = new EthereumJsonSerializer().Serialize(rpcBlock);

            json.Contains("withdrawals\"", StringComparison.Ordinal).Should().Be(item.Expected);
            json.Contains("withdrawalsRoot", StringComparison.Ordinal).Should().Be(item.Expected);
        }

        // With (Block, bool), tests don't run for some reason. Flipped to (bool, Block).
        private static IEnumerable<(bool, Block)> BlockForRpcTestSource() =>
            new[]
    {
                (true, Build.A.Block
                    .WithWithdrawals(new[]
        {
                        Build.A.Withdrawal
                    .WithAmount(1)
                    .WithRecipient(TestItem.AddressA)
                            .TestObject
                    })
                .TestObject
            ),

            (false, Build.A.Block.WithWithdrawals(null).TestObject)
        };
    }
}
