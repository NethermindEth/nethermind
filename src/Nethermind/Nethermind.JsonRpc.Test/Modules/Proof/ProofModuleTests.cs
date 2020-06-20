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
using Nethermind.Dirichlet.Numerics;
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
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Proof
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture(true, true)]
    [TestFixture(true, false)]
    [TestFixture(false, false)]
    public class ProofModuleTests
    {
        private readonly bool _createSystemAccount;
        private readonly bool _useNonZeroGasPrice;
        private IProofModule _proofModule;
        private IBlockTree _blockTree;
        private IDbProvider _dbProvider;
        private TestSpecProvider _specProvider;

        public ProofModuleTests(bool createSystemAccount, bool useNonZeroGasPrice)
        {
            _createSystemAccount = createSystemAccount;
            _useNonZeroGasPrice = useNonZeroGasPrice;
        }
        
        [SetUp]
        public void Setup()
        {
            InMemoryReceiptStorage receiptStorage = new InMemoryReceiptStorage();
            _specProvider = new TestSpecProvider(Homestead.Instance);
            _blockTree = Build.A.BlockTree().WithTransactions(receiptStorage, _specProvider).OfChainLength(10).TestObject;
            _dbProvider = new MemDbProvider();
            ProofModuleFactory moduleFactory = new ProofModuleFactory(
                _dbProvider,
                _blockTree,
                new CompositeDataRecoveryStep(new TxSignaturesRecoveryStep(new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance), NullTxPool.Instance, LimboLogs.Instance)),
                receiptStorage,
                _specProvider,
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

            // missing with header
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_getTransactionReceipt", $"{txHash}");
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
            Assert.GreaterOrEqual(receiptWithProof.ReceiptProof.Last().Length, 256 /* bloom length */);
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
            
            _proofModule.proof_call(tx, new BlockParameter(block.Number));

            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{serializer.Serialize(tx)}", $"{block.Number}");
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
            _proofModule.proof_call(tx, new BlockParameter(block.Hash));

            EthereumJsonSerializer serializer = new EthereumJsonSerializer();
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{serializer.Serialize(tx)}", $"{block.Hash}");
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

            CallResultWithProof callResultWithProof = _proofModule.proof_call(tx, new BlockParameter(blockOnTop.Number)).Data;
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
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{serializer.Serialize(tx)}", $"{blockOnTop.Number}");
            Assert.True(response.Contains("\"result\""));
            
            return callResultWithProof;
        }

        private void TestCallWithStorageAndCode(byte[] code, UInt256 gasPrice, Address from = null)
        {
            StateProvider stateProvider = CreateInitialState(code);
            StorageProvider storageProvider = new StorageProvider(_dbProvider.StateDb, stateProvider, LimboLogs.Instance);
            for (int i = 0; i < 10000; i++)
            {
                storageProvider.Set(new StorageCell(TestItem.AddressB, new UInt256(i)), i.ToBigEndianByteArray());
            }

            storageProvider.Commit();
            storageProvider.CommitTrees();

            stateProvider.Commit(MainnetSpecProvider.Instance.GenesisSpec, null);
            stateProvider.CommitTree();

            _dbProvider.StateDb.Commit();

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

            CallResultWithProof callResultWithProof = _proofModule.proof_call(tx, new BlockParameter(blockOnTop.Number)).Data;
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
            string response = RpcTest.TestSerializedRequest(_proofModule, "proof_call", $"{serializer.Serialize(tx)}", $"{blockOnTop.Number}");
            Assert.True(response.Contains("\"result\""));
        }

        private StateProvider CreateInitialState(byte[] code)
        {
            StateProvider stateProvider = new StateProvider(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
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

            return stateProvider;
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

            stateProvider.Commit(MainnetSpecProvider.Instance.GenesisSpec, null);
            stateProvider.CommitTree();
            _dbProvider.CodeDb.Commit();
            _dbProvider.StateDb.Commit();
        }
    }
}