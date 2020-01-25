//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Dynamic;
using Jint.Native;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Cli.Modules;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Cli.Test
{
    public class ProofCliModuleTests
    {
        private IProofModule _proofModule;
        private IBlockTree _blockTree;
        private IDbProvider _dbProvider;
        private ICliConsole _cliConsole = Substitute.For<ICliConsole>();
        private EthereumJsonSerializer _serializer;
        private IJsonRpcClient _jsonRpcClient;
        private CliEngine _engine;

        [SetUp]
        public void Setup()
        {
            InMemoryReceiptStorage receiptStorage = new InMemoryReceiptStorage();
            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            _blockTree = Build.A.BlockTree().WithTransactions(receiptStorage, specProvider).OfChainLength(10).TestObject;
            _dbProvider = new MemDbProvider();
            ProofModuleFactory moduleFactory = new ProofModuleFactory(
                _dbProvider,
                _blockTree,
                new CompositeDataRecoveryStep(),
                receiptStorage,
                specProvider,
                LimboLogs.Instance);

            _proofModule = moduleFactory.Create();
            
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
           Keccak txHash = _blockTree.FindBlock(1).Transactions[0].Hash;
            var result = _proofModule.proof_getTransactionByHash(txHash, includeHeader);

            JsonRpcResponse response = new JsonRpcResponse
            {
                Id = "id1",
                JsonRpc = "2",
                Result = result
            };
            
            _jsonRpcClient.Post<object>("proof_getTransactionByHash", txHash, includeHeader)
                .Returns(_serializer.Serialize(response));

            JsValue value = _engine.Execute($"proof.getTransactionByHash(\"{txHash}\", {(includeHeader ? "true" : "false")})");
            Console.WriteLine(_serializer.Serialize(value.ToObject(), true));
            Assert.AreNotEqual(JsValue.Null, value);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Get_transaction_receipt(bool includeHeader)
        {
            Keccak txHash = _blockTree.FindBlock(1).Transactions[0].Hash;
            var result = _proofModule.proof_getTransactionByHash(txHash, includeHeader);

            JsonRpcResponse response = new JsonRpcResponse
            {
                Id = "id1",
                JsonRpc = "2",
                Result = result,
            };
            _jsonRpcClient.Post<object>("proof_getTransactionReceipt", txHash, includeHeader)
                .Returns(_serializer.Serialize(response));

            JsValue value = _engine.Execute($"proof.getTransactionReceipt(\"{txHash}\", {(includeHeader ? "true" : "false")})");
            Console.WriteLine(_serializer.Serialize(value.ToObject(), true));
            Assert.AreNotEqual(JsValue.Null, value);
        }

        [Test]
        public void Call()
        {
            StateProvider stateProvider = new StateProvider(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
            AddAccount(stateProvider, TestItem.AddressA, 1.Ether());
            AddAccount(stateProvider, TestItem.AddressB, 1.Ether());

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            TransactionForRpc tx = new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB
            };

            var result = _proofModule.proof_call(tx, new BlockParameter(block.Hash));
            JsonRpcResponse response = new JsonRpcResponse
            {
                Id = "id1",
                JsonRpc = "2",
                Result = result,
            };
            
            _jsonRpcClient.Post<object>("proof_call", Arg.Any<ExpandoObject>(), block.Hash.ToString())
                .Returns(_serializer.Serialize(response));

            JsValue value = _engine.Execute($"proof.call({_serializer.Serialize(tx)}, \"{block.Hash}\")");
            Console.WriteLine(_serializer.Serialize(value.ToObject(), true));
            Assert.AreNotEqual(JsValue.Null, value);
        }

        private void AddAccount(StateProvider stateProvider, Address account, UInt256 initialBalance)
        {
            stateProvider.CreateAccount(account, initialBalance);
            stateProvider.Commit(MuirGlacier.Instance, null);
            stateProvider.CommitTree();
            _dbProvider.StateDb.Commit();
        }
    }
}