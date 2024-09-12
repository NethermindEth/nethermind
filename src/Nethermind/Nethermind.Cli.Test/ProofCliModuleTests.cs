// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Dynamic;
using Jint.Native;
using Nethermind.Cli.Console;
using Nethermind.Cli.Modules;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Cli.Test
{
    public class ProofCliModuleTests
    {
        private readonly ICliConsole _cliConsole = Substitute.For<ICliConsole>();
        private EthereumJsonSerializer _serializer;
        private IJsonRpcClient _jsonRpcClient;
        private CliEngine _engine;
        private NodeManager _nodeManager;

        [SetUp]
        public void Setup()
        {
            _serializer = new EthereumJsonSerializer();
            _jsonRpcClient = Substitute.For<IJsonRpcClient>();
            _engine = new CliEngine(_cliConsole);
            _nodeManager = new(_engine, _serializer, _cliConsole, LimboLogs.Instance);
            _nodeManager.SwitchClient(_jsonRpcClient);
            ICliConsole cliConsole = Substitute.For<ICliConsole>();
            CliModuleLoader moduleLoader = new(_engine, _nodeManager, cliConsole);
            moduleLoader.LoadModule(typeof(ProofCliModule));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Get_transaction_by_hash(bool includeHeader)
        {
            Hash256 txHash = TestItem.KeccakA;
            JsonRpcSuccessResponse response = new()
            {
                Id = "id1",
                Result = "result"
            };

            _jsonRpcClient.Post<object>("proof_getTransactionByHash", txHash, includeHeader)
                .Returns(_serializer.Serialize(response));

            JsValue value = _engine.Execute($"proof.getTransactionByHash(\"{txHash}\", {(includeHeader ? "true" : "false")})");
            Colorful.Console.WriteLine(_serializer.Serialize(value.ToObject(), true));
            Assert.That(value, Is.Not.EqualTo(JsValue.Null));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Get_transaction_receipt(bool includeHeader)
        {
            Hash256 txHash = TestItem.KeccakA;
            JsonRpcSuccessResponse response = new()
            {
                Id = "id1",
                Result = "result",
            };

            _jsonRpcClient.Post<object>("proof_getTransactionReceipt", txHash, includeHeader)
                .Returns(_serializer.Serialize(response));

            JsValue value = _engine.Execute($"proof.getTransactionReceipt(\"{txHash}\", {(includeHeader ? "true" : "false")})");
            Colorful.Console.WriteLine(_serializer.Serialize(value.ToObject(), true));
            Assert.That(value, Is.Not.EqualTo(JsValue.Null));
        }

        [Test]
        public void Call()
        {
            Hash256 blockHash = TestItem.KeccakA;
            TransactionForRpc tx = new()
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB
            };

            JsonRpcSuccessResponse response = new()
            {
                Id = "id1",
                Result = "result",
            };

            _jsonRpcClient.Post<object>("proof_call", Arg.Any<ExpandoObject>(), blockHash.ToString())
                .Returns(_serializer.Serialize(response));

            JsValue value = _engine.Execute($"proof.call({_serializer.Serialize(tx)}, \"{blockHash}\")");
            Colorful.Console.WriteLine(_serializer.Serialize(value.ToObject(), true));
            Assert.That(value, Is.Not.EqualTo(JsValue.Null));
        }

        [Test]
        public void Syncing_false()
        {
            _jsonRpcClient.Post<object>("eth_syncing").Returns(false);
            var result = _nodeManager.PostJint("eth_syncing").Result;
            Assert.That(result, Is.EqualTo(JsValue.False));
        }
    }
}
