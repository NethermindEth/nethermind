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

using System.Dynamic;
using Jint.Native;
using Nethermind.Cli.Console;
using Nethermind.Cli.Modules;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
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
        private ICliConsole _cliConsole = Substitute.For<ICliConsole>();
        private EthereumJsonSerializer _serializer;
        private IJsonRpcClient _jsonRpcClient;
        private CliEngine _engine;

        [SetUp]
        public void Setup()
        {
            _serializer = new EthereumJsonSerializer();
            _jsonRpcClient = Substitute.For<IJsonRpcClient>();
            _engine = new CliEngine(_cliConsole);
            NodeManager nodeManager = new NodeManager(_engine, _serializer, _cliConsole, LimboLogs.Instance);
            nodeManager.SwitchClient(_jsonRpcClient);
            ICliConsole cliConsole = Substitute.For<ICliConsole>();
            CliModuleLoader moduleLoader = new CliModuleLoader(_engine, nodeManager, cliConsole);
            moduleLoader.LoadModule(typeof(ProofCliModule));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Get_transaction_by_hash(bool includeHeader)
        {
            Keccak txHash = TestItem.KeccakA;
            JsonRpcSuccessResponse response = new JsonRpcSuccessResponse
            {
                Id = "id1",
                Result = "result"
            };

            _jsonRpcClient.Post<object>("proof_getTransactionByHash", txHash, includeHeader)
                .Returns(_serializer.Serialize(response));

            JsValue value = _engine.Execute($"proof.getTransactionByHash(\"{txHash}\", {(includeHeader ? "true" : "false")})");
            Colorful.Console.WriteLine(_serializer.Serialize(value.ToObject(), true));
            Assert.AreNotEqual(JsValue.Null, value);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Get_transaction_receipt(bool includeHeader)
        {
            Keccak txHash = TestItem.KeccakA;
            JsonRpcSuccessResponse response = new JsonRpcSuccessResponse
            {
                Id = "id1",
                Result = "result",
            };

            _jsonRpcClient.Post<object>("proof_getTransactionReceipt", txHash, includeHeader)
                .Returns(_serializer.Serialize(response));

            JsValue value = _engine.Execute($"proof.getTransactionReceipt(\"{txHash}\", {(includeHeader ? "true" : "false")})");
            Colorful.Console.WriteLine(_serializer.Serialize(value.ToObject(), true));
            Assert.AreNotEqual(JsValue.Null, value);
        }

        [Test]
        public void Call()
        {
            Keccak blockHash = TestItem.KeccakA;
            TransactionForRpc tx = new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB
            };

            JsonRpcSuccessResponse response = new JsonRpcSuccessResponse
            {
                Id = "id1",
                Result = "result",
            };

            _jsonRpcClient.Post<object>("proof_call", Arg.Any<ExpandoObject>(), blockHash.ToString())
                .Returns(_serializer.Serialize(response));

            JsValue value = _engine.Execute($"proof.call({_serializer.Serialize(tx)}, \"{blockHash}\")");
            Colorful.Console.WriteLine(_serializer.Serialize(value.ToObject(), true));
            Assert.AreNotEqual(JsValue.Null, value);
        }
    }
}
