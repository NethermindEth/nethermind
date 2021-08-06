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

using System;
using System.IO;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Db.Blooms;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Test.Modules.Proof
{
    [Parallelizable(ParallelScope.None)]
    [TestFixture(true, true)]
    [TestFixture(true, false)]
    [TestFixture(false, false)]
    public class ProofRpcModuleTests
    {
        private readonly bool _createSystemAccount;
        private readonly bool _useNonZeroGasPrice;
        private IProofRpcModule _proofRpcModule;
        private IBlockTree _blockTree;
        private IDbProvider _dbProvider;
        private TestSpecProvider _specProvider;

        public ProofRpcModuleTests(bool createSystemAccount, bool useNonZeroGasPrice)
        {
            _createSystemAccount = createSystemAccount;
            _useNonZeroGasPrice = useNonZeroGasPrice;
        }
        
        [SetUp]
        public async Task Setup()
        {
            InMemoryReceiptStorage receiptStorage = new InMemoryReceiptStorage();
            _specProvider = new TestSpecProvider(London.Instance);
            _blockTree = Build.A.BlockTree().WithTransactions(receiptStorage, _specProvider).OfChainLength(10).TestObject;
            _dbProvider = await TestMemDbProvider.InitAsync();

            ProofModuleFactory moduleFactory = new ProofModuleFactory(
                _dbProvider,
                _blockTree,
                new TrieStore(_dbProvider.StateDb, LimboLogs.Instance).AsReadOnly(),
                new CompositeBlockPreprocessorStep(new RecoverSignatures(new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), NullTxPool.Instance, _specProvider, LimboLogs.Instance)),
                receiptStorage,
                _specProvider,
                LimboLogs.Instance);

            _proofRpcModule = moduleFactory.Create();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Can_get_transaction(bool withHeader)
        {
            Keccak txHash = _blockTree.FindBlock(1).Transactions[0].Hash;
            TransactionWithProof txWithProof = _proofRpcModule.proof_getTransactionByHash(txHash, withHeader).Data;
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

            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains("\"result\""));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void When_getting_non_existing_tx_correct_error_code_is_returned(bool withHeader)
        {
            Keccak txHash = TestItem.KeccakH;
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains($"{ErrorCodes.ResourceNotFound}"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void When_getting_non_existing_receipt_correct_error_code_is_returned(bool withHeader)
        {
            Keccak txHash = TestItem.KeccakH;
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", $"{txHash}", $"{withHeader}");
            Assert.True(response.Contains($"{ErrorCodes.ResourceNotFound}"));
        }

        [Test]
        public void On_incorrect_params_returns_correct_error_code()
        {
            Keccak txHash = TestItem.KeccakH;

            // missing with header
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", $"{txHash}");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "missing");

            // too many
            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", $"{txHash}", "true", "false");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "too many");

            // missing with header
            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", $"{txHash}");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "missing");

            // too many
            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionByHash", $"{txHash}", "true", "false");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "too many");

            // all wrong
            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{txHash}");
            Assert.True(response.Contains($"{ErrorCodes.InvalidParams}"), "missing");
        }

        [TestCase(true, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0xc50c34035d0045dae3d949cb7625eea6c826fb755116ead701de9b8d7edeeb29\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0xb1e7593b3eea16f8caddf3f185858f92f7a9b32db8368821a70a48340479a531\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"to\":null,\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a011f2d93515d9963f68e6746135f7a786a37ae47ac5b18a5e9fb2e8e9dbf23fad80808080808080a07c3834793d56420b91a53b153d0a67a0ab32cecd250dbc197130eb17e88f32538080808080808080\",\"0xf86530b862f860800182520894000000000000000000000000000000000000000001818024a0b4e030f395ed357d206b58d9a0ded408589a9e26f1a5b41010772cd0d84b8d16a04d9797a972bc308ea635f22455881c41c7c9fb946c93db6f99d2bd529675af13\"],\"receiptProof\":[\"0xf851a053e4a8d7d8438fa45d6b75bbd6fb699b08049c1caf1c21ada42a746ddfb61d0b80808080808080a04de834bd23b53a3d82923ae5f359239b326c66758f2ae636ab934844dba2b9658080808080808080\",\"0xf9010f30b9010bf9010880825208b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"],\"blockHeader\":\"0xf901f9a0b3157bcccab04639f6393042690a6c9862deebe88c781f911e8dfd265531e9ffa01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a0c5054cffd7f5a0b215b5df35420edb8059cc8585f8201dd31e5e10436437364ca0e1b1585a222beceb3887dc6701802facccf186c2d0f6aa69e26ae0c431fc2b5db9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000830f424001833d090080830f424183010203a02ba5557a4c62a513c7e56d1bf13373e0da6bec016755483e91589fe1c6d212e28800000000000003e8\"},\"id\":67}")]
        [TestCase(false, "{\"jsonrpc\":\"2.0\",\"result\":{\"receipt\":{\"transactionHash\":\"0xc50c34035d0045dae3d949cb7625eea6c826fb755116ead701de9b8d7edeeb29\",\"transactionIndex\":\"0x0\",\"blockHash\":\"0xb1e7593b3eea16f8caddf3f185858f92f7a9b32db8368821a70a48340479a531\",\"blockNumber\":\"0x1\",\"cumulativeGasUsed\":\"0x0\",\"gasUsed\":\"0x0\",\"effectiveGasPrice\":\"0x1\",\"to\":null,\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x0\",\"type\":\"0x0\"},\"txProof\":[\"0xf851a011f2d93515d9963f68e6746135f7a786a37ae47ac5b18a5e9fb2e8e9dbf23fad80808080808080a07c3834793d56420b91a53b153d0a67a0ab32cecd250dbc197130eb17e88f32538080808080808080\",\"0xf86530b862f860800182520894000000000000000000000000000000000000000001818024a0b4e030f395ed357d206b58d9a0ded408589a9e26f1a5b41010772cd0d84b8d16a04d9797a972bc308ea635f22455881c41c7c9fb946c93db6f99d2bd529675af13\"],\"receiptProof\":[\"0xf851a053e4a8d7d8438fa45d6b75bbd6fb699b08049c1caf1c21ada42a746ddfb61d0b80808080808080a04de834bd23b53a3d82923ae5f359239b326c66758f2ae636ab934844dba2b9658080808080808080\",\"0xf9010f30b9010bf9010880825208b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0\"]},\"id\":67}")]
        public void Can_get_receipt(bool withHeader, string expectedResult)
        {
            Keccak txHash = _blockTree.FindBlock(1).Transactions[0].Hash;
            ReceiptWithProof receiptWithProof = _proofRpcModule.proof_getTransactionReceipt(txHash, withHeader).Data;
            Assert.NotNull(receiptWithProof.Receipt);
            Assert.AreEqual(2, receiptWithProof.ReceiptProof.Length);
            Assert.GreaterOrEqual(receiptWithProof.ReceiptProof.Last().Length, 256 /* bloom length */);
            if (withHeader)
            {
                Assert.NotNull(receiptWithProof.BlockHeader);
            }
            else
            {
                Assert.Null(receiptWithProof.BlockHeader);
            }

            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_getTransactionReceipt", $"{txHash}", $"{withHeader}");
            Assert.AreEqual(expectedResult, response);
        }

        [Test]
        public void Can_call()
        {
            StateProvider stateProvider = CreateInitialState(null);

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            // would need to setup state root somehow...

            TransactionForRpc tx = new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB,
                GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
            };
            
            _proofRpcModule.proof_call(tx, new BlockParameter(block.Number));

            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{block.Number}");
            Assert.True(response.Contains("\"result\""));
        }

        [Test]
        public void Can_call_by_hash()
        {
            StateProvider stateProvider = CreateInitialState(null);

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            // would need to setup state root somehow...

            TransactionForRpc tx = new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB,
                GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
            };
            _proofRpcModule.proof_call(tx, new BlockParameter(block.Hash));

            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{block.Hash}");
            Assert.True(response.Contains("\"result\""));
        }

        [Test]
        public void Can_call_by_hash_canonical()
        {
            Block lastHead = _blockTree.Head;
            Block block = Build.A.Block.WithParent(lastHead).TestObject;
            Block newBlockOnMain = Build.A.Block.WithParent(lastHead).WithDifficulty(block.Difficulty + 1).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);
            BlockTreeBuilder.AddBlock(_blockTree, newBlockOnMain);

            // would need to setup state root somehow...

            TransactionForRpc tx = new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB,
                GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
            };

            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{{\"blockHash\" : \"{block.Hash}\", \"requireCanonical\" : true}}");
            Assert.True(response.Contains("-32000"));

            response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{{\"blockHash\" : \"{TestItem.KeccakG}\", \"requireCanonical\" : true}}");
            Assert.True(response.Contains("-32001"));
        }

        [Test]
        public void Can_call_with_block_hashes()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.BlockHeaders.Length);
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
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(3, result.BlockHeaders.Length);
        }

        [Test]
        public void Can_call_with_same_block_hash_many_time()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.BlockHeaders.Length);
        }

        [Test]
        public void Can_call_with_storage_load()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .Done;

            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
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
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_storage_write()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .Done;

            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodecopy()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x20")
                .PushData("0x00")
                .PushData("0x00")
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODECOPY)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodecopy_to_system_account()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x20")
                .PushData("0x00")
                .PushData("0x00")
                .PushData(Address.SystemUser)
                .Op(Instruction.EXTCODECOPY)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodesize()
        {
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODESIZE)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodesize_to_system_account()
        {
            byte[] code = Prepare.EvmCode
                .PushData(Address.SystemUser)
                .Op(Instruction.EXTCODESIZE)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodehash()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_extcodehash_to_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(Address.SystemUser)
                .Op(Instruction.EXTCODEHASH)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_just_basic_addresses()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .Op(Instruction.STOP)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_balance()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .Done;

            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }
        
        [Test]
        public void Can_call_with_self_balance()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .Op(Instruction.SELFBALANCE)
                .Done;

            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_balance_of_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(Address.SystemUser)
                .Op(Instruction.BALANCE)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_call_to_system_account_with_zero_value()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(Address.SystemUser)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_static_call_to_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(Address.SystemUser)
                .PushData(1000000)
                .Op(Instruction.STATICCALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_delegate_call_to_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(Address.SystemUser)
                .PushData(1000000)
                .Op(Instruction.DELEGATECALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_call_to_system_account_with_non_zero_value()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(1)
                .PushData(Address.SystemUser)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_call_with_zero_value()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_static_call()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.STATICCALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_delegate_call()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.DELEGATECALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(3, result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_call_with_non_zero_value()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(0)
                .PushData(1)
                .PushData(TestItem.AddressC)
                .PushData(1000000)
                .Op(Instruction.CALL)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_self_destruct()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.SELFDESTRUCT)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);

            Assert.AreEqual(2 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
        }

        [Test]
        public void Can_call_with_self_destruct_to_system_account()
        {
            _specProvider.SpecToReturn = MuirGlacier.Instance;
            byte[] code = Prepare.EvmCode
                .PushData(Address.SystemUser)
                .Op(Instruction.SELFDESTRUCT)
                .Done;
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(2, result.Accounts.Length);
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
            CallResultWithProof result = TestCallWithCode(code);
            Assert.AreEqual(1 + (_useNonZeroGasPrice ? 1 : 0), result.Accounts.Length);
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

        [Test]
        public void Can_call_with_mix_of_everything_and_storage()
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

            TestCallWithStorageAndCode(code, _useNonZeroGasPrice ? 10.GWei() : 0);
        }
        
        [Test]
        public void Can_call_with_mix_of_everything_and_storage_from_another_account_wrong_nonce()
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

            TestCallWithStorageAndCode(code, 0, TestItem.AddressD);
        }

        private CallResultWithProof TestCallWithCode(byte[] code, Address from = null)
        {
            StateProvider stateProvider = CreateInitialState(code);

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).WithBeneficiary(TestItem.AddressD).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);
            Block blockOnTop = Build.A.Block.WithParent(block).WithStateRoot(root).WithBeneficiary(TestItem.AddressD).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, blockOnTop);

            // would need to setup state root somehow...

            TransactionForRpc tx = new TransactionForRpc
            {
                From = from,
                To = TestItem.AddressB,
                GasPrice = _useNonZeroGasPrice ? 10.GWei() : 0
            };

            CallResultWithProof callResultWithProof = _proofRpcModule.proof_call(tx, new BlockParameter(blockOnTop.Number)).Data;
            Assert.Greater(callResultWithProof.Accounts.Length, 0);

            foreach (AccountProof accountProof in callResultWithProof.Accounts)
            {
                ProofVerifier.Verify(accountProof.Proof, block.StateRoot);
                foreach (StorageProof storageProof in accountProof.StorageProofs)
                {
                    ProofVerifier.Verify(storageProof.Proof, accountProof.StorageRoot);
                }
            }

            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{blockOnTop.Number}");
            Assert.True(response.Contains("\"result\""));
            
            return callResultWithProof;
        }

        private void TestCallWithStorageAndCode(byte[] code, UInt256 gasPrice, Address from = null)
        {
            StateProvider stateProvider = CreateInitialState(code);
            StorageProvider storageProvider = new StorageProvider(new TrieStore(_dbProvider.StateDb, LimboLogs.Instance), stateProvider, LimboLogs.Instance);

            for (int i = 0; i < 10000; i++)
            {
                storageProvider.Set(new StorageCell(TestItem.AddressB, (UInt256)i), i.ToBigEndianByteArray());
            }

            storageProvider.Commit();
            storageProvider.CommitTrees(0);

            stateProvider.Commit(MainnetSpecProvider.Instance.GenesisSpec, NullStateTracer.Instance);
            stateProvider.CommitTree(0);

            Keccak root = stateProvider.StateRoot;

            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);
            Block blockOnTop = Build.A.Block.WithParent(block).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, blockOnTop);

            // would need to setup state root somehow...

            TransactionForRpc tx = new TransactionForRpc
            {
                // we are testing system transaction here when From is null
                From = from,
                To = TestItem.AddressB,
                GasPrice = gasPrice,
                Nonce = 1000
            };

            CallResultWithProof callResultWithProof = _proofRpcModule.proof_call(tx, new BlockParameter(blockOnTop.Number)).Data;
            Assert.Greater(callResultWithProof.Accounts.Length, 0);

            // just the keys for debugging
            Span<byte> span = stackalloc byte[32];
            new UInt256(0).ToBigEndian(span);
            Keccak k0 = Keccak.Compute(span);

            // just the keys for debugging
            new UInt256(1).ToBigEndian(span);
            Keccak k1 = Keccak.Compute(span);

            // just the keys for debugging
            new UInt256(2).ToBigEndian(span);
            Keccak k2 = Keccak.Compute(span);

            foreach (AccountProof accountProof in callResultWithProof.Accounts)
            {
                // this is here for diagnostics - so you can read what happens in the test
                // generally the account here should be consistent with the values inside the proof
                // the exception will be thrown if the account did not exist before the call
                Account account;
                try
                {
                    account = new AccountDecoder().Decode(new RlpStream(ProofVerifier.Verify(accountProof.Proof, block.StateRoot)));
                }
                catch (Exception)
                {
                    // ignored
                }

                foreach (StorageProof storageProof in accountProof.StorageProofs)
                {
                    // we read the values here just to allow easier debugging so you can confirm that the value is same as the one in the proof and in the trie
                    byte[] value = ProofVerifier.Verify(storageProof.Proof, accountProof.StorageRoot);
                }
            }

            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofRpcModule, "proof_call", $"{serializer.Serialize(tx)}", $"{blockOnTop.Number}");
            Assert.True(response.Contains("\"result\""));
        }

        private StateProvider CreateInitialState(byte[] code)
        {
            StateProvider stateProvider = new StateProvider(new TrieStore(_dbProvider.StateDb, LimboLogs.Instance), _dbProvider.CodeDb, LimboLogs.Instance);
            AddAccount(stateProvider, TestItem.AddressA, 1.Ether());
            AddAccount(stateProvider, TestItem.AddressB, 1.Ether());

            if (code != null)
            {
                AddCode(stateProvider, TestItem.AddressB, code);
            }

            if (_createSystemAccount)
            {
                AddAccount(stateProvider, Address.SystemUser, 1.Ether());
            }

            stateProvider.CommitTree(0);

            return stateProvider;
        }

        private void AddAccount(StateProvider stateProvider, Address account, UInt256 initialBalance)
        {
            stateProvider.CreateAccount(account, initialBalance);
            stateProvider.Commit(MuirGlacier.Instance, NullStateTracer.Instance);
        }

        private void AddCode(StateProvider stateProvider, Address account, byte[] code)
        {
            Keccak codeHash = stateProvider.UpdateCode(code);
            stateProvider.UpdateCodeHash(account, codeHash, MuirGlacier.Instance);

            stateProvider.Commit(MainnetSpecProvider.Instance.GenesisSpec, NullStateTracer.Instance);
        }
    }
}
