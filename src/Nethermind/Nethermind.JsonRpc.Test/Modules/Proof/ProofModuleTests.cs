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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Store;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Proof
{
    [TestFixture]
    public class ProofModuleTests
    {
        private IProofModule _proofModule;
        private IBlockTree _blockTree;
        private IDbProvider _dbProvider;

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
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Can_get_transaction(bool withHeader)
        {
            Keccak txHash = _blockTree.FindBlock(1).Transactions[0].Hash;
            TransactionWithProof txWithProof = _proofModule.proof_getTransactionByHash(txHash, withHeader).Data;
            Assert.NotNull(txWithProof.Transaction);
            Assert.AreEqual(2, txWithProof.TxProof.Length);
            if (withHeader)
            {
                Assert.NotNull(txWithProof.BlockHeader);
            }
            else
            {
                Assert.Null(txWithProof.BlockHeader);
            }
            
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_getTransactionByHash", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains("\"result\""));
        }
        
        [TestCase(true)]
        [TestCase(false)]
        public void When_getting_non_existing_tx_correct_error_code_is_returned(bool withHeader)
        {
            Keccak txHash = TestItem.KeccakH;
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_getTransactionByHash", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains($"{ErrorCodes.ResourceNotFound}"));
        }
        
        [TestCase(true)]
        [TestCase(false)]
        public void When_getting_non_existing_receipt_correct_error_code_is_returned(bool withHeader)
        {
            Keccak txHash = TestItem.KeccakH;
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_getTransactionReceipt", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains($"{ErrorCodes.ResourceNotFound}"));
        }

        [Test]
        public void On_incorrect_params_returns_correct_error_code()
        {
            Keccak txHash = TestItem.KeccakH;
            string response;

            // missing with header
            response = RpcTest.TestSerializedRequest(_proofModule, "proof_getTransactionReceipt", $"{txHash}");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "missing");
            
            // too many
            response = RpcTest.TestSerializedRequest(_proofModule, "proof_getTransactionReceipt", $"{txHash}", "true", "false");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "too many");
            
            // missing with header
            response = RpcTest.TestSerializedRequest(_proofModule, "proof_getTransactionByHash", $"{txHash}");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "missing");
            
            // too many
            response = RpcTest.TestSerializedRequest(_proofModule, "proof_getTransactionByHash", $"{txHash}", "true", "false");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "too many");
            
            // all wrong
            response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{txHash}");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "missing");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Can_get_receipt(bool withHeader)
        {
            Keccak txHash = _blockTree.FindBlock(1).Transactions[0].Hash;
            ReceiptWithProof receiptWithProof = _proofModule.proof_getTransactionReceipt(txHash, withHeader).Data;
            Assert.NotNull(receiptWithProof.Receipt);
            Assert.AreEqual(2, receiptWithProof.ReceiptProof.Length);
            if (withHeader)
            {
                Assert.NotNull(receiptWithProof.BlockHeader);
            }
            else
            {
                Assert.Null(receiptWithProof.BlockHeader);
            }
            
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_getTransactionReceipt", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains("\"result\""));
        }

        [Test]
        public void Can_call()
        {
            StateProvider stateProvider = new StateProvider(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
            AddAccount(stateProvider, TestItem.AddressA, 1.Ether());
            AddAccount(stateProvider, TestItem.AddressB, 1.Ether());

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            // would need to setup state root somehow...

            TransactionForRpc tx = new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB
            };
            _proofModule.proof_call(tx, new BlockParameter(block.Number));
            
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{serializer.Serialize(tx)}", $"{block.Number}");
            Assert.True(response.Contains("\"result\""));
        }
        
        [Test]
        public void Can_call_by_hash()
        {
            StateProvider stateProvider = new StateProvider(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
            AddAccount(stateProvider, TestItem.AddressA, 1.Ether());
            AddAccount(stateProvider, TestItem.AddressB, 1.Ether());

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            // would need to setup state root somehow...

            TransactionForRpc tx = new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB
            };
            _proofModule.proof_call(tx, new BlockParameter(block.Hash));
            
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{serializer.Serialize(tx)}", $"{block.Hash}");
            Assert.True(response.Contains("\"result\""));
        }
        
        [Test]
        public void Can_call_by_hash_canonical()
        {
            BlockHeader lastHead = _blockTree.Head;
            Block block = Build.A.Block.WithParent(lastHead).TestObject;
            Block newBlockOnMain = Build.A.Block.WithParent(lastHead).WithDifficulty(block.Difficulty + 1).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);
            BlockTreeBuilder.AddBlock(_blockTree, newBlockOnMain);

            // would need to setup state root somehow...

            TransactionForRpc tx = new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB
            };
            
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{serializer.Serialize(tx)}", $"{{\"blockHash\" : \"{block.Hash}\", \"requireCanonical\" : true}}");
            Assert.True(response.Contains("-32000"));
            
            response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{serializer.Serialize(tx)}", $"{{\"blockHash\" : \"{TestItem.KeccakG}\", \"requireCanonical\" : true}}");
            Assert.True(response.Contains("-32001"));
        }

        [Test]
        public void Can_call_with_block_hashes()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .Done;
            TestCallWithCode(code);
        }
        
        [Test]
        public void Can_call_with_many_block_hashes()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x02")
                .Op(Instruction.BLOCKHASH)
                .Done;
            TestCallWithCode(code);
        }

        [Test]
        public void Can_call_with_storage_load()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .Done;
            TestCallWithCode(code);
        }
        
        [Test]
        public void Can_call_with_many_storage_loads()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .PushData("0x02")
                .Op(Instruction.SLOAD)
                .Done;
            TestCallWithCode(code);
        }

        [Test]
        public void Can_call_with_storage_write()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .Done;
            TestCallWithCode(code);
        }
        
        [Test]
        public void Can_call_with_many_storage_writes()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .PushData("0x02")
                .PushData("0x02")
                .Op(Instruction.SSTORE)
                .Done;
            TestCallWithCode(code);
        }
        
        [Test]
        public void Can_call_with_mix_of_everything()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x02")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .PushData("0x02")
                .Op(Instruction.SLOAD)
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .PushData("0x03")
                .PushData("0x03")
                .Op(Instruction.SSTORE)
                .Done;
            TestCallWithCode(code);
        }

        private void TestCallWithCode(byte[] code)
        {
            StateProvider stateProvider = new StateProvider(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
            AddAccount(stateProvider, TestItem.AddressA, 1.Ether());
            AddAccount(stateProvider, TestItem.AddressB, 1.Ether());
            AddCode(stateProvider, TestItem.AddressB, code);

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            // would need to setup state root somehow...

            TransactionForRpc tx = new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB
            };
            
            _proofModule.proof_call(tx, new BlockParameter(block.Number));
            
            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{serializer.Serialize(tx)}", $"{block.Number}");
            Assert.True(response.Contains("\"result\""));
        }

        private void AddAccount(StateProvider stateProvider, Address account, UInt256 initialBalance)
        {
            stateProvider.CreateAccount(account, initialBalance);
            stateProvider.Commit(MuirGlacier.Instance, null);
            stateProvider.CommitTree();
            _dbProvider.StateDb.Commit();
        }

        private void AddCode(StateProvider stateProvider, Address account, byte[] code)
        {
            Keccak codeHash = stateProvider.UpdateCode(code);
            stateProvider.UpdateCodeHash(account, codeHash, MuirGlacier.Instance);

            stateProvider.Commit(MainNetSpecProvider.Instance.GenesisSpec, null);
            stateProvider.CommitTree();
            _dbProvider.CodeDb.Commit();
            _dbProvider.StateDb.Commit();
        }
    }
}